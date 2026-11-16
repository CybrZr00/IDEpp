namespace IdePlusPlus.Shell.Team;

/// <summary>
/// One run of a builder's work being fast-forwarded onto the team branch, handed to
/// <see cref="TeamToolDispatcher.MergeLanded"/> after the merge has actually happened.
///
/// It carries the merge range rather than only the new head because the specialist lanes review
/// exactly what changed: a mutation tester mutates the lines this range touched, the conformity lane
/// formats the files it names, the adversary reads it. The event is a value so the queue can hold it
/// (and the run can hand it to <c>team_next_review</c>) long after the merge step returned.
/// </summary>
/// <param name="Author">The worker whose commit landed, as its role name - b2, mut1 - so a review
/// item can say whose work it is.</param>
/// <param name="From">The team branch head before the fast-forward.</param>
/// <param name="To">The team branch head after it; From..To is the merge.</param>
public sealed record MergeLandedEvent(string Author, string From, string To);
