using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Core.Config;

public interface IConfigStore<T> where T : class, new()
{
    T Current { get; }

    /// <summary>Raised after the file changed on disk and reloaded cleanly.</summary>
    event EventHandler<T>? Changed;

    Task SaveAsync(T value, CancellationToken ct = default);
}

/// <summary>A config file that exists but cannot be read, said in words a person can act on.</summary>
/// <param name="Detail">The parser's own message, which names the line and what it expected.</param>
public sealed record ConfigProblem(string FileName, string Message, string Detail);

/// <summary>
/// What a config file is doing, apart from its contents: whether it is there at all, and whether
/// the version on disk is one we could read. Separate from <see cref="IConfigStore{T}"/> so the
/// window can watch every file the same way without knowing what each one holds.
/// </summary>
public interface IConfigFile
{
    string FileName { get; }

    /// <summary>
    /// True when the file is on disk. The one reason to write defaults is that it is not — never
    /// that its contents came out empty, which is also what a file that fails to parse looks like.
    /// </summary>
    bool FileExists { get; }

    /// <summary>What is wrong with the file on disk right now, or null when it read cleanly.</summary>
    ConfigProblem? Problem { get; }

    /// <summary>Raised when an edit on disk fails to parse. Raised on a worker thread.</summary>
    event EventHandler<ConfigProblem>? ProblemFound;

    /// <summary>
    /// Changes the file as text, atomically, leaving everything the edit does not touch exactly as
    /// it was — comments included, which saving a parsed value would lose. The change reloads the
    /// way a hand edit does.
    /// </summary>
    Task EditTextAsync(Func<string, string> edit, CancellationToken ct = default);
}

/// <summary>
/// A hand-editable JSONC file that hot-reloads. These files are meant to be opened in an editor
/// (§7), so comments and trailing commas are accepted, writes are atomic, and — importantly — a
/// file that fails to parse keeps the last good value in memory rather than silently reverting
/// the user's rules to defaults.
/// </summary>
/// <remarks>
/// At startup there is no last good value to keep, so a file that fails to parse there loads as
/// empty — and is then never written over without a copy being put aside first. It used to be:
/// startup saw "no tools", took that for a first run, and saved the defaults over the file the
/// user had been editing.
/// </remarks>
public sealed class JsonConfigStore<T> : IConfigStore<T>, IConfigFile, IDisposable where T : class, new()
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // These files are written to be read and hand-edited. The default encoder escapes "+" as
        // +, which turns a keybinding into line noise; relaxed escaping is only a concern
        // when embedding JSON in HTML, which never happens here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The same, on one line: for a file the application keeps for itself and nobody edits, where
    /// indentation is only more to write and to read back.
    /// </summary>
    private static readonly JsonSerializerOptions CompactOptions = new(SerializerOptions) { WriteIndented = false };

    private readonly string _path;
    private readonly ILogger _log;
    private readonly JsonSerializerOptions _writeOptions;
    private readonly FileSystemWatcher _watcher;
    private readonly Debouncer _reload = new(TimeSpan.FromMilliseconds(250));
    private readonly Lock _gate = new();

    private T _current;
    private DateTime _ownWrite;
    private DateTime _seenWrite;
    private ConfigProblem? _problem;
    private int _rereads;

    /// <param name="compact">Written on one line (<see cref="CompactOptions"/>).</param>
    public JsonConfigStore(string directory, string fileName, ILogger logger, bool compact = false)
    {
        _path = Path.Combine(directory, fileName);
        _log = logger;
        _writeOptions = compact ? CompactOptions : SerializerOptions;
        Directory.CreateDirectory(directory);
        _current = LoadAtStartup();

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileTouched;
        _watcher.Created += OnFileTouched;
        _watcher.Renamed += OnFileTouched;
        _watcher.EnableRaisingEvents = true;
    }

    public T Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event EventHandler<T>? Changed;

    public string FileName => Path.GetFileName(_path);

    public bool FileExists => File.Exists(_path);

    public ConfigProblem? Problem
    {
        get
        {
            lock (_gate)
                return _problem;
        }
    }

    public event EventHandler<ConfigProblem>? ProblemFound;

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        bool broken;
        lock (_gate)
        {
            _current = value;
            // Also a file changed on disk since it was last read or written here, whatever the
            // reason it was not reloaded (a save inside the reload's delay, a reload given up on):
            // what is in it now is not what is in memory, and only one of them is about to stay.
            broken = _problem is not null ||
                     (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _seenWrite && _seenWrite != default);
        }

        // The file on disk is one we could not read, which means it is one the user was editing
        // (or one another program held at startup) and we do not know what is in it. Writing over
        // it — adding a bookmark to a bookmarks.json with a missing comma — would throw their work
        // away to save ours, so it goes aside first. Only once the copy exists is the problem
        // cleared: a copy that failed used to leave the next save free to write over the file.
        if (broken && File.Exists(_path))
        {
            var aside = Path.Combine(
                Path.GetDirectoryName(_path)!,
                $"{Path.GetFileNameWithoutExtension(_path)}.broken-{DateTime.Now:yyyy-MM-dd-HHmmss-fff}{Path.GetExtension(_path)}");
            try
            {
                File.Copy(_path, aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logged as the write's failure is: at exit nothing else would say it (review, 2026-10-28).
                _log.LogWarning(ex, "Could not save {File}: its unreadable version could not be kept aside first", FileName);
                throw;
            }
            _log.LogWarning(
                "{File} could not be read and is being replaced; the old version is kept as {Aside}",
                FileName, Path.GetFileName(aside));
        }

        lock (_gate)
            _problem = null;

        // ConfigureAwait(false) throughout: this is library code, and a caller that blocks on it
        // from the UI thread must not deadlock on a continuation waiting for that same thread.
        var temp = TempName();
        var created = false;
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
            created = true;
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, _writeOptions, ct).ConfigureAwait(false);
                // On the disk before it takes the name: after a power cut the name must hold the
                // old version or the new one, never zeros.
                stream.Flush(flushToDisk: true);
            }

            Replace(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said here, once for every caller: one saving at exit has no one to tell.
            _log.LogWarning(ex, "Could not save {File}", FileName);
            throw;
        }
        finally
        {
            // Only a temp this call made: if CreateNew failed, the name was someone else's.
            if (created)
                DeleteQuietly(temp);
        }
    }

    /// <summary>Our own temp file, if a failed write left it.</summary>
    private static void DeleteQuietly(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
#pragma warning disable RS0030 // §1.6: our own temp file, a GUID name beside the config, left by a failed write.
                File.Delete(temp);
#pragma warning restore RS0030
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task EditTextAsync(Func<string, string> edit, CancellationToken ct = default)
    {
        // One edit at a time: two quick choices in the settings window both read the same text,
        // and the second write dropped the first change.
        await _edits.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EditOnceAsync(edit, ct).ConfigureAwait(false);
        }
        finally
        {
            _edits.Release();
        }
    }

    private readonly SemaphoreSlim _edits = new(1, 1);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private async Task EditOnceAsync(Func<string, string> edit, CancellationToken ct)
    {
        // A missing file is edited from nothing: settings.json is not written until something in
        // it is changed, and the first change is made this way.
        var missing = !File.Exists(_path);
        var original = missing ? [] : await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);

        // Strictly UTF-8: a file saved as ANSI decoded leniently had its ä turned into U+FFFD, and
        // the edit wrote that back for good. Refused instead, as a file that does not parse is.
        var bom = original.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(original, bom ? Bom.Length : 0, original.Length - (bom ? Bom.Length : 0));
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new JsonException($"{FileName} is not saved as UTF-8, so it is not edited: saving it back would change its other characters.", ex);
        }

        var edited = edit(text);
        if (edited == text)
            return;

        var temp = TempName();
        var created = false;
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
            created = true;
            await using (stream.ConfigureAwait(false))
            {
                if (bom)
                    await stream.WriteAsync(Bom, ct).ConfigureAwait(false);
                await stream.WriteAsync(new System.Text.UTF8Encoding(false).GetBytes(edited), ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Not marked as our own: an edit reloads the way a hand edit does. A file that was
            // missing is moved in without overwriting, so one that appeared meanwhile — the user
            // saving it from an editor — is not replaced by an edit of nothing; the move fails.
            if (missing)
            {
                File.Move(temp, _path);
                return;
            }

            // Saved from an editor since it was read: that version is the user's latest, and the
            // edit was made to an older one. Refused rather than written over it. The window left
            // between this look and the move is the move's own few microseconds.
            var now = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            if (!now.AsSpan().SequenceEqual(original))
                throw new IOException($"{FileName} changed while it was being edited, so the edit was not written.");

#pragma warning disable RS0030 // §1.6: the config's next version, whole and flushed to disk, takes its name in one step; the version it replaces was checked to be the one the edit was made to.
            File.Move(temp, _path, overwrite: true);
#pragma warning restore RS0030
        }
        finally
        {
            // Only a temp this call made: if CreateNew failed, the name was someone else's.
            if (created)
                DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// Its own name per write: two saves at once shared one ".tmp", so the second failed to open
    /// it and its change never reached the disk.
    /// </summary>
    private string TempName() => $"{_path}.{Guid.NewGuid():N}.tmp";

    /// <summary>Swaps a written temp file in, and remembers the write as ours.</summary>
    private void Replace(string temp)
    {
#pragma warning disable RS0030 // §1.6: the config's next version, whole and flushed to disk, takes its name in one step; one
        // that could not be read was copied aside first (SaveAsync).
        File.Move(temp, _path, overwrite: true);
#pragma warning restore RS0030
        lock (_gate)
            _ownWrite = _seenWrite = File.GetLastWriteTimeUtc(_path);
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
            _rereads = 0;
        _reload.Schedule(Reload);
    }

    private void Reload()
    {
        // Our own write bouncing back in. Recognised by its time rather than by a quiet period
        // after saving: a hand edit that arrived inside that period was ignored, and the next save
        // wrote stale memory over it.
        lock (_gate)
        {
            if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) == _ownWrite)
                return;
        }

        var reloaded = Load(startup: false, out var unreadable);
        if (unreadable)
        {
            // An editor in the middle of replacing the file. Asked again shortly rather than given
            // up on, which left the old version in memory to be saved over the edit.
            bool again;
            lock (_gate)
                again = ++_rereads < 20;
            if (again)
            {
                _reload.Schedule(Reload);
                return;
            }

            // Given up on: the edit on disk is not what is in memory, so the next save keeps a
            // copy of it first instead of writing over it.
            lock (_gate)
            {
                _problem = new ConfigProblem(
                    FileName,
                    $"{FileName} changed but could not be read, so the version from before is still in use.",
                    "If it is saved over, the version on disk is kept beside it first.");
            }
            return;
        }

        if (reloaded is null)
            return;

        lock (_gate)
            _current = reloaded;

        _log.LogInformation("Reloaded {File}", Path.GetFileName(_path));
        Changed?.Invoke(this, reloaded);
    }

    /// <summary>
    /// The file's contents, waiting a little for one another program has open. One that still
    /// cannot be read is a problem like a parse error: it loaded as empty and counted as healthy,
    /// and the first save (bookmarks wrote theirs at once) replaced it with nothing kept.
    /// </summary>
    private T LoadAtStartup()
    {
        for (var attempt = 0; ; attempt++)
        {
            var loaded = Load(startup: true, out var unreadable);
            if (!unreadable)
                return loaded ?? new T();

            if (attempt == 5)
            {
                lock (_gate)
                {
                    _problem = new ConfigProblem(
                        FileName,
                        $"{FileName} could not be read (another program may have it open), so none of it is loaded.",
                        "If it is saved over, the version on disk is kept beside it first.");
                }
                return new T();
            }

            Thread.Sleep(100);
        }
    }

    private T? Load(bool startup, out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(_path))
            return null;

        try
        {
            var stamp = File.GetLastWriteTimeUtc(_path);
            using var stream = File.OpenRead(_path);
            var loaded = JsonSerializer.Deserialize<T>(stream, SerializerOptions);
            lock (_gate)
            {
                _problem = null;
                _seenWrite = stamp;
            }
            return loaded;
        }
        catch (JsonException ex)
        {
            // Keep the last good value. Reverting to defaults here would quietly discard whatever
            // the user was in the middle of editing.
            _log.LogError(ex, "{File} could not be parsed; keeping the previous version", FileName);

            // Line and column, one-based as every editor counts them, so the message leads
            // straight to the mistake. The consequence differs: at startup there was nothing
            // earlier to fall back on.
            var where = ex.LineNumber is { } line
                ? $" at line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}"
                : string.Empty;
            var problem = new ConfigProblem(
                FileName,
                startup
                    ? $"{FileName} has an error{where}, so none of it is loaded. Fix it and it loads by itself."
                    : $"{FileName} has an error{where}. The version from before your edit is still in use.",
                ex.Message);

            lock (_gate)
                _problem = problem;
            if (!startup)
                ProblemFound?.Invoke(this, problem);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "{File} could not be read", Path.GetFileName(_path));
            unreadable = true;
            return null;
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _reload.Dispose();
        _edits.Dispose();
    }
}

/// <summary>Runs the most recent action once the caller stops asking for a while.</summary>
public sealed class Debouncer(TimeSpan delay) : IDisposable
{
    /// <summary>
    /// The last save, as the application closes, waited for. A failure is the store's to log and
    /// goes no further: thrown out of a Dispose, it stopped the container disposing the rest, and
    /// one full disk or locked file skipped every later save (history, view state, the log).
    /// </summary>
    public static void FlushAtExit(Func<Task> flush)
    {
        try
        {
            flush().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        catch (Exception ex)
        {
            // Anything else stops the rest as surely (a snapshot that could not be built, the
            // serializer refusing a value), and is not the store's to have logged (review, 2026-10-28).
            ExitFailure?.Invoke(ex);
        }
    }

    /// <summary>Where a save at exit that failed in an unexpected way is said: the application's log.</summary>
    public static Action<Exception>? ExitFailure { get; set; }

    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;

    public void Schedule(Action action)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            cts = _pending = new CancellationTokenSource();
        }

        _ = Task.Delay(delay, cts.Token).ContinueWith(
            task =>
            {
                if (!task.IsCanceled)
                    action();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
