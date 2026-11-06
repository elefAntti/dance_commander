using DanceCommander.Core.Decisions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A pane's questions are asked in that pane: the scope it sets reaches a provider's worker
/// thread, and doesn't outlive the work that set it.
/// </summary>
public sealed class DecisionScopeTests
{
    [Fact]
    public async Task TheScopeFollowsTheWorkAndNotTheCaller()
    {
        var pane = new ScriptedDecisions();
        IDecisionHost? seenByProvider = null;

        async Task ListAsync()
        {
            DecisionScope.Current = pane;
            await Task.Yield();
            seenByProvider = await Task.Run(() => DecisionScope.Current);
        }

        await ListAsync();

        seenByProvider.ShouldBeSameAs(pane);
        DecisionScope.Current.ShouldBeNull();
    }
}
