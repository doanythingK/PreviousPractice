namespace PreviousPractice.Models;

public sealed record ImportedQuestionsSaveResult(
    int AddedQuestionCount,
    int UpdatedQuestionCount,
    int RemovedQuestionCount,
    bool StructureChanged)
{
    public int AffectedQuestionCount =>
        AddedQuestionCount + UpdatedQuestionCount + RemovedQuestionCount;
}
