using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class QueryGroupingModelTests
{
    [Fact]
    public void GroupedSnapshotUsesTheSameTreeForPreviewAndEvaluation()
    {
        var draft = new QueryEditorModel(["A", "B", "C"], [SearchOperator.And, SearchOperator.Or]);
        var rows = draft.GetRows();
        Assert.True(draft.TryGroup([rows[1].Id, rows[2].Id], SearchOperator.Or, out var error), error);
        var snapshot = draft.Snapshot;
        Assert.Equal("A AND (B OR C)", snapshot.Formula);
        Assert.Equal(snapshot.Formula, SearchFormula.Format(snapshot.Expression!, snapshot.Terms));
        for (var bits = 0; bits < 8; bits++)
        {
            var present = new[] { (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0 };
            Assert.Equal(present[0] && (present[1] || present[2]), SearchFormula.Evaluate(snapshot.Expression!, present));
        }
    }

    [Fact]
    public void NestedGroupAndUngroupPreserveOrderedPositionIdentityAndPreviewFlattening()
    {
        var draft = new QueryEditorModel(["A", "B", "C"]);
        var original = draft.GetRows();
        Assert.True(draft.TryGroup([original[1].Id, original[2].Id], SearchOperator.Or, out _));
        var group = Assert.Single(draft.GetRows(), row => row.IsGroup);
        Assert.True(draft.TryGroup([group.Id], SearchOperator.And, out _));
        Assert.Equal("A AND ((B OR C))", draft.Snapshot.Formula);
        Assert.Equal(2, draft.GetRows().Count(row => row.IsGroup));
        var outer = draft.GetRows().First(row => row.IsGroup);
        Assert.True(draft.TryUngroup([outer.Id], out _));
        Assert.Equal("A AND (B OR C)", draft.Snapshot.Formula);
        Assert.True(draft.TryUngroup([group.Id], out _));
        Assert.Equal("(A AND B) OR C", draft.Snapshot.Formula);
        Assert.False(SearchFormula.Evaluate(new BinaryExpression(SearchOperator.And, new TermExpression(0),
            new GroupExpression(new BinaryExpression(SearchOperator.Or, new TermExpression(1), new TermExpression(2)))), [false, false, true]));
        Assert.True(SearchFormula.Evaluate(draft.Snapshot.Expression!, [false, false, true]));
        Assert.Equal(original.Select(row => row.Id), draft.GetRows().Select(row => row.Id));
    }

    [Fact]
    public void DeleteAChildPrunesEmptyGroupsWithoutLosingOtherLiteralPositions()
    {
        var draft = new QueryEditorModel(["A", "B", "C"]);
        var rows = draft.GetRows();
        Assert.True(draft.TryGroup([rows[1].Id, rows[2].Id], SearchOperator.Or, out _));
        Assert.True(draft.TryDelete([rows[1].Id], out _));
        Assert.Equal("A AND (C)", draft.Snapshot.Formula);
        Assert.Equal(new[] { "A", "C" }, draft.Snapshot.Terms);
        Assert.True(draft.TryDelete([rows[2].Id], out _));
        Assert.Equal("A", draft.Snapshot.Formula);
        Assert.DoesNotContain(draft.GetRows(), row => row.IsGroup);
        Assert.True(draft.TryDelete([rows[0].Id], out _));
        Assert.Empty(draft.Snapshot.Terms); Assert.Null(draft.Snapshot.Expression);
    }

    [Fact]
    public void DuplicateAndOperatorLookingLiteralsAreIndependentPositions()
    {
        var draft = new QueryEditorModel(["AND", "()"]);
        Assert.True(draft.TryAddLiteral("AND", SearchOperator.Or, null, out _));
        var rows = draft.GetRows();
        Assert.Equal(3, rows.Select(row => row.Id).Distinct().Count());
        Assert.Equal("(\"AND\" AND \"()\") OR \"AND\"", draft.Snapshot.Formula);
        Assert.True(draft.TryGroup([rows[1].Id, rows[2].Id], SearchOperator.Or, out _));
        Assert.Equal("\"AND\" AND (\"()\" OR \"AND\")", draft.Snapshot.Formula);
        Assert.True(draft.TryDelete([rows[2].Id], out _));
        Assert.Equal(new[] { "AND", "()" }, draft.Snapshot.Terms);
        Assert.Equal(rows[0].Id, draft.GetRows()[0].Id);
    }

    [Fact]
    public void NonAdjacentCrossParentAndUnknownSelectionsLeaveTheDraftUnchanged()
    {
        var draft = new QueryEditorModel(["A", "B", "C"]);
        var rows = draft.GetRows(); var before = draft.Snapshot;
        Assert.False(draft.TryGroup([rows[0].Id, rows[2].Id], SearchOperator.Or, out var error));
        Assert.Contains("相邻", error); Assert.Same(before, draft.Snapshot);
        Assert.True(draft.TryGroup([rows[1].Id, rows[2].Id], SearchOperator.Or, out _));
        before = draft.Snapshot;
        Assert.False(draft.TryGroup([rows[0].Id, rows[1].Id], SearchOperator.And, out error));
        Assert.Contains("同一", error); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryDelete([rows[0].Id, rows[1].Id], out _)); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryGroup([Guid.NewGuid()], SearchOperator.And, out _)); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryGroup([], SearchOperator.And, out _)); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryUngroup([rows[1].Id], out _)); Assert.Same(before, draft.Snapshot);
    }

    [Fact]
    public void ResourceLimitRejectionsRetainTheExactDraftAndLiteralInput()
    {
        var draft = new QueryEditorModel(["A", "B"], limits: new SearchResourceLimits { MaxTerms = 2, MaxTermCharacters = 3 });
        var before = draft.Snapshot;
        Assert.False(draft.TryAddLiteral("C", SearchOperator.And, null, out var error));
        Assert.Contains("2", error); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryAddLiteral("long", SearchOperator.And, null, out error));
        Assert.Contains("3", error); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryAddLiteral(" ", SearchOperator.And, null, out _)); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TrySetConnector(draft.GetRows()[1].Id, (SearchOperator)99, out _)); Assert.Same(before, draft.Snapshot);
        Assert.False(draft.TryAddLiteral("C", SearchOperator.Or, Guid.NewGuid(), out _)); Assert.Same(before, draft.Snapshot);
    }

    [Fact]
    public void DeepNestingIsRejectedBeforeTheSnapshotChanges()
    {
        var draft = new QueryEditorModel(["A"]);
        for (var i = 0; i < 127; i++)
            Assert.True(draft.TryGroup([draft.GetRows()[0].Id], SearchOperator.And, out var error), error);
        var before = draft.Snapshot;
        Assert.False(draft.TryGroup([draft.GetRows()[0].Id], SearchOperator.And, out var failure));
        Assert.Contains("128", failure); Assert.Same(before, draft.Snapshot);
        Assert.True(SearchFormula.Evaluate(before.Expression!, [true]));
    }

    [Fact]
    public void NodeBudgetIsRejectedBeforeTheSnapshotChanges()
    {
        var draft = new QueryEditorModel(Enumerable.Range(0, 64).Select(index => "K" + index).ToArray());
        // A 64-leaf flat tree has 127 nodes. Wrap each leaf twice (255), then one once (256).
        foreach (var row in draft.GetRows().ToArray())
        {
            Assert.True(draft.TryGroup([row.Id], SearchOperator.And, out _));
            var wrapper = draft.GetRows().First(item => item.IsGroup &&
                draft.GetRows().Any(child => child.ParentId == item.Id && child.Id == row.Id));
            Assert.True(draft.TryGroup([wrapper.Id], SearchOperator.And, out _));
        }
        Assert.True(draft.TryGroup([draft.GetRows()[0].Id], SearchOperator.And, out _));
        var before = draft.Snapshot;
        Assert.False(draft.TryGroup([draft.GetRows()[0].Id], SearchOperator.And, out var error));
        Assert.Contains("256", error); Assert.Same(before, draft.Snapshot);
    }

    [Fact]
    public void CloneMutationsNeverWriteThroughToTheOriginalSnapshot()
    {
        var original = new QueryEditorModel(["A", "B", "C"]);
        var before = original.Snapshot;
        var clone = original.Clone();
        Assert.True(clone.TryGroup(clone.GetRows().Skip(1).Select(row => row.Id).ToArray(), SearchOperator.Or, out _));
        Assert.Equal("A AND (B OR C)", clone.Snapshot.Formula);
        Assert.Same(before, original.Snapshot); Assert.Equal("A AND B AND C", original.Snapshot.Formula);
    }
}
