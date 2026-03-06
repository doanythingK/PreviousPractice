namespace PreviousPractice.Models;

public readonly record struct QuestionNumberRange(int StartIndex, int EndIndex)
{
    public int Count => EndIndex - StartIndex + 1;

    public bool Contains(int index)
    {
        return index >= StartIndex && index <= EndIndex;
    }

    public override string ToString()
    {
        return $"{StartIndex}-{EndIndex}";
    }
}
