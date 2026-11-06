using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DanceCommander.Core.Config;

/// <summary>
/// Sets or removes one property of a hand-edited JSONC file by changing only the text it occupies
/// (SETTINGS_PLAN.md §3). Saving the parsed object instead would throw away every comment the user
/// wrote and reformat the rest, to change one value.
/// </summary>
/// <remarks>
/// A path is an array of property names, not a dotted string: a key in <c>"keys"</c> is a command
/// id, and command ids have dots in them. Names match case-insensitively, as the loader reads them;
/// where a name appears twice the last one is the one in force, so that is the one edited. A file
/// that does not parse is refused with the parser's <see cref="JsonException"/>: an edit can only
/// be placed in a structure that can be read.
/// </remarks>
public static class JsoncEdit
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The text with the property at <paramref name="path"/> holding <paramref name="value"/>:
    /// replaced where it is, or added at the end of the deepest object on the path that exists,
    /// with the objects below it created.
    /// </summary>
    public static string Set(string json, IReadOnlyList<string> path, JsonNode? value)
    {
        if (path.Count == 0)
            throw new ArgumentException("A property needs a name.", nameof(path));

        var text = string.IsNullOrWhiteSpace(json) ? "{" + NewLineOf(json) + "}" + NewLineOf(json) : json;
        var bytes = Encoding.UTF8.GetBytes(text);
        var newline = NewLineOf(text);
        var parent = Parse(bytes);

        // Down as far as the file goes. What is missing becomes one new member of the deepest
        // object found, nested as deep as needed. A null in the way ("keys": null) becomes the
        // object the path needs; anything else there is the user's and is refused, not replaced.
        for (var depth = 0; ; depth++)
        {
            var member = parent.Find(path[depth]);
            if (member is null)
                return Insert(bytes, parent, path[depth], Wrap(path, depth + 1, value), newline);

            if (depth < path.Count - 1 && member.Value is null && !member.IsNull)
                throw new JsonException($"\"{Describe(path.Take(depth + 1).ToList())}\" is not an object, so nothing can go inside it.");

            if (depth == path.Count - 1 || member.Value is null)
            {
                var replacement = Format(Wrap(path, depth + 1, value), IndentOfLine(bytes, member.NameStart), newline);
                return Splice(bytes, member.ValueStart, member.ValueEnd, replacement);
            }

            parent = member.Value;
        }
    }

    /// <summary>The value inside objects named by the path from <paramref name="first"/> on.</summary>
    private static JsonNode? Wrap(IReadOnlyList<string> path, int first, JsonNode? value)
    {
        for (var i = path.Count - 1; i >= first; i--)
            value = new JsonObject { [path[i]] = value };
        return value;
    }

    /// <summary>
    /// The text without the property at <paramref name="path"/>, and without any object on the path
    /// that is left empty by it, so a file of sparse settings does not fill with <c>{}</c>s. The text
    /// unchanged when the property is not there.
    /// </summary>
    public static string Remove(string json, IReadOnlyList<string> path)
    {
        // Every occurrence: the loader takes the last of duplicate names, so taking out only one
        // would bring an earlier one into force rather than the default.
        for (var text = json; ;)
        {
            var removed = RemoveOne(text, path);
            if (ReferenceEquals(removed, text))
                return text;
            text = removed;
        }
    }

    private static string RemoveOne(string json, IReadOnlyList<string> path)
    {
        if (path.Count == 0 || string.IsNullOrWhiteSpace(json))
            return json;

        var bytes = Encoding.UTF8.GetBytes(json);
        var root = Parse(bytes);

        // The chain of objects down to the property, so an emptied one can go with it.
        var chain = new List<(ObjectSpan Owner, Member Member)>();
        var owner = root;
        foreach (var name in path)
        {
            var member = owner.Find(name);
            if (member is null)
                return json;
            chain.Add((owner, member));
            if (chain.Count < path.Count)
            {
                if (member.Value is not { } child)
                    return json;
                owner = child;
            }
        }

        // The highest member whose removal leaves nothing behind it but empty objects. Judged by
        // the bytes, not the members: an object holding a comment of the user's is not empty,
        // and taking it would take the comment.
        var cut = chain.Count - 1;
        while (cut > 0 && chain[cut].Owner.Members.Count == 1 && HoldsOnly(bytes, chain[cut].Owner, chain[cut].Member))
            cut--;

        var (from, to, comma) = MemberSpan(bytes, chain[cut].Owner, chain[cut].Member);
        var removed = Splice(bytes, from, to, string.Empty);
        if (comma < 0)
            return removed;

        // The comma comes before the member, so taking it out moves nothing already measured.
        var text = Encoding.UTF8.GetBytes(removed);
        return Splice(text, comma, comma + 1, string.Empty);
    }

    /// <summary>True when the object holds the member and nothing else but space and a comma.</summary>
    private static bool HoldsOnly(byte[] bytes, ObjectSpan owner, Member member)
    {
        static bool Blank(byte[] bytes, long from, long to)
        {
            for (var i = from; i < to; i++)
            {
                if (bytes[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)','))
                    return false;
            }
            return true;
        }

        return Blank(bytes, owner.Open + 1, member.NameStart) && Blank(bytes, member.ValueEnd, owner.Close - 1);
    }

    /// <summary>A path as dotted text, for messages and the settings window's muted key.</summary>
    public static string Describe(IReadOnlyList<string> path) => string.Join('.', path);

    private static string Insert(byte[] bytes, ObjectSpan parent, string name, JsonNode? value, string newline)
    {
        if (parent.Members.Count > 0)
        {
            var last = parent.Members[^1];
            var indent = IndentOfLine(bytes, last.NameStart);
            var member = indent + Quote(name) + ": " + Format(value, indent, newline);

            // After a trailing comma the new member keeps the style: it gets one of its own.
            var after = SkipSpaceAndComments(bytes, last.ValueEnd);
            return after < bytes.Length && bytes[after] == (byte)','
                ? Splice(bytes, after + 1, after + 1, newline + member + ",")
                : Splice(bytes, last.ValueEnd, last.ValueEnd, "," + newline + member);
        }

        // An empty object, or one holding only comments: the member goes on a line of its own,
        // indented one step further than the line the object opens on.
        var outer = IndentOfLine(bytes, parent.Open);
        var inner = outer + "  ";
        var text = newline + inner + Quote(name) + ": " + Format(value, inner, newline);
        var close = parent.Close - 1;
        var onItsOwnLine = LineStart(bytes, close) is var start && IsBlank(bytes, start, close);
        return onItsOwnLine
            ? Splice(bytes, start - NewLineLength(bytes, start), start - NewLineLength(bytes, start), text)
            : Splice(bytes, close, close, text + newline + outer);
    }

    /// <summary>
    /// The bytes a member takes, with the comma after it and, when it has its lines to itself, those
    /// whole lines. The last member has no comma after it, so the one before gives up its own
    /// instead, alone: what sits between the two (a comment on the line before) stays.
    /// </summary>
    private static (long From, long To, long CommaBefore) MemberSpan(byte[] bytes, ObjectSpan owner, Member member)
    {
        long from = member.NameStart, to = member.ValueEnd, commaBefore = -1;

        var after = SkipSpaceAndComments(bytes, to);
        var index = owner.Members.IndexOf(member);
        if (after < bytes.Length && bytes[after] == (byte)',')
        {
            to = after + 1;
        }
        else if (index > 0)
        {
            var at = SkipSpaceAndComments(bytes, owner.Members[index - 1].ValueEnd);
            if (at < from && bytes[at] == (byte)',')
                commaBefore = at;
        }

        // Whole lines, when nothing else shares them.
        var lineStart = LineStart(bytes, from);
        var lineEnd = LineEnd(bytes, to);
        if (IsBlank(bytes, lineStart, from) && IsBlank(bytes, to, lineEnd))
        {
            from = lineStart;
            to = lineEnd + NewLineLengthAt(bytes, lineEnd);
        }

        return (from, to, commaBefore);
    }

    private static ObjectSpan Parse(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The file does not hold a JSON object.");

        var root = ReadObject(ref reader);

        // Trailing tokens after the root make the file unreadable to the loader too.
        if (reader.Read())
            throw new JsonException("The file has more after its closing brace.");
        return root;
    }

    /// <summary>Reads an object whose opening brace the reader is on.</summary>
    private static ObjectSpan ReadObject(ref Utf8JsonReader reader)
    {
        var span = new ObjectSpan { Open = reader.TokenStartIndex };
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                span.Close = reader.BytesConsumed;
                return span;
            }

            var member = new Member { Name = reader.GetString()!, NameStart = reader.TokenStartIndex };
            reader.Read();
            member.ValueStart = reader.TokenStartIndex;
            member.IsNull = reader.TokenType == JsonTokenType.Null;
            if (reader.TokenType == JsonTokenType.StartObject)
                member.Value = ReadObject(ref reader);
            else if (reader.TokenType == JsonTokenType.StartArray)
                reader.Skip();
            member.ValueEnd = reader.BytesConsumed;
            span.Members.Add(member);
        }

        throw new JsonException("The file ends inside an object.");
    }

    private static string Format(JsonNode? value, string indent, string newline)
    {
        var text = value?.ToJsonString(JsonConfigStore<object>.SerializerOptions) ?? "null";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(newline, lines.Select((line, i) => i == 0 ? line : indent + line));
    }

    private static string Quote(string name) => JsonSerializer.Serialize(name, JsonConfigStore<object>.SerializerOptions);

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static long SkipSpace(byte[] bytes, long at)
    {
        while (at < bytes.Length && bytes[at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            at++;
        return at;
    }

    /// <summary>
    /// Past space and comments, as the reader goes between tokens: a comma after a comment
    /// (<c>"a": 1 /* c */,</c>) is still the comma after the value. Looking only past space found
    /// none there, and removing the first member left a leading comma no reader accepts.
    /// </summary>
    private static long SkipSpaceAndComments(byte[] bytes, long at)
    {
        while (true)
        {
            at = SkipSpace(bytes, at);
            if (at + 1 >= bytes.Length || bytes[at] != (byte)'/')
                return at;
            if (bytes[at + 1] == (byte)'/')
            {
                at = LineEnd(bytes, at);
            }
            else if (bytes[at + 1] == (byte)'*')
            {
                var end = at + 2;
                while (end + 1 < bytes.Length && !(bytes[end] == (byte)'*' && bytes[end + 1] == (byte)'/'))
                    end++;
                at = Math.Min(end + 2, bytes.Length);
            }
            else
            {
                return at;
            }
        }
    }

    private static long LineStart(byte[] bytes, long at)
    {
        while (at > 0 && bytes[at - 1] != (byte)'\n')
            at--;
        return at;
    }

    private static long LineEnd(byte[] bytes, long at)
    {
        while (at < bytes.Length && bytes[at] is not ((byte)'\r' or (byte)'\n'))
            at++;
        return at;
    }

    /// <summary>The length of the line break that ends just before <paramref name="lineStart"/>.</summary>
    private static int NewLineLength(byte[] bytes, long lineStart) =>
        lineStart == 0 ? 0 : lineStart >= 2 && bytes[lineStart - 2] == (byte)'\r' ? 2 : 1;

    /// <summary>The length of the line break that starts at <paramref name="at"/>.</summary>
    private static int NewLineLengthAt(byte[] bytes, long at) =>
        at >= bytes.Length ? 0 : bytes[at] == (byte)'\r' && at + 1 < bytes.Length && bytes[at + 1] == (byte)'\n' ? 2 : 1;

    private static bool IsBlank(byte[] bytes, long from, long to)
    {
        for (var i = from; i < to; i++)
        {
            if (bytes[i] is not ((byte)' ' or (byte)'\t'))
                return false;
        }
        return true;
    }

    private static string IndentOfLine(byte[] bytes, long at)
    {
        var start = LineStart(bytes, at);
        var end = start;
        while (end < bytes.Length && bytes[end] is (byte)' ' or (byte)'\t')
            end++;
        return Encoding.UTF8.GetString(bytes, (int)start, (int)(end - start));
    }

    private static string Splice(byte[] bytes, long from, long to, string insert) =>
        Encoding.UTF8.GetString(bytes, 0, (int)from) + insert + Encoding.UTF8.GetString(bytes, (int)to, bytes.Length - (int)to);

    private sealed class ObjectSpan
    {
        /// <summary>Where the opening brace is.</summary>
        public long Open { get; init; }

        /// <summary>Just past the closing brace.</summary>
        public long Close { get; set; }

        public List<Member> Members { get; } = [];

        public Member? Find(string name) =>
            Members.LastOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Member
    {
        public required string Name { get; init; }

        public long NameStart { get; init; }

        public long ValueStart { get; set; }

        public long ValueEnd { get; set; }

        public bool IsNull { get; set; }

        /// <summary>The value, when it is an object.</summary>
        public ObjectSpan? Value { get; set; }
    }
}
