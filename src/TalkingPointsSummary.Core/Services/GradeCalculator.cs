using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Calculates the current grade level for a child based on their starting grade,
/// starting year, and the current date. Students advance one grade every September 1st.
/// </summary>
public interface IGradeCalculator
{
    int GetCurrentGrade(Child child);
    int GetCurrentGrade(Child child, DateTime currentDate);
    int GetCurrentGrade(int startingGrade, int startingYear);
    int GetCurrentGrade(int startingGrade, int startingYear, DateTime currentDate);
    int GetCurrentSchoolYear();
    int GetCurrentSchoolYear(DateTime currentDate);
    string GetGradeLabel(int grade);
    string GetCurrentGradeLabel(Child child);
    string GetCurrentGradeLabel(Child child, DateTime currentDate);
}

public sealed class GradeCalculator(TimeProvider? timeProvider = null) : IGradeCalculator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Returns the current grade as an integer (0 = Kindergarten).
    /// </summary>
    public int GetCurrentGrade(Child child)
    {
        return GetCurrentGrade(child, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Returns the current grade as an integer (0 = Kindergarten).
    /// </summary>
    public int GetCurrentGrade(Child child, DateTime currentDate)
    {
        return GetCurrentGrade(child.StartingGrade, child.StartingYear, currentDate);
    }

    /// <summary>
    /// Returns the current grade as an integer (0 = Kindergarten).
    /// </summary>
    public int GetCurrentGrade(int startingGrade, int startingYear)
    {
        return GetCurrentGrade(startingGrade, startingYear, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Returns the current grade as an integer (0 = Kindergarten).
    /// </summary>
    public int GetCurrentGrade(int startingGrade, int startingYear, DateTime currentDate)
    {
        var currentSchoolYear = GetCurrentSchoolYear(currentDate);
        var yearsAdvanced = currentSchoolYear - startingYear;
        return startingGrade + Math.Max(0, yearsAdvanced);
    }

    public int GetCurrentSchoolYear()
    {
        return GetCurrentSchoolYear(_timeProvider.GetUtcNow().UtcDateTime);
    }

    public int GetCurrentSchoolYear(DateTime currentDate)
    {
        return currentDate.Month >= 9 ? currentDate.Year : currentDate.Year - 1;
    }

    /// <summary>
    /// Returns the grade label (e.g., "Kindergarten", "1st Grade", "2nd Grade").
    /// </summary>
    public string GetGradeLabel(int grade)
    {
        return grade switch
        {
            0 => "Kindergarten",
            1 => "1st Grade",
            2 => "2nd Grade",
            3 => "3rd Grade",
            _ when grade >= 4 && grade <= 12 => $"{grade}th Grade",
            _ => $"Grade {grade}"
        };
    }

    /// <summary>
    /// Returns the full grade label for a child at the current date.
    /// </summary>
    public string GetCurrentGradeLabel(Child child)
    {
        return GetCurrentGradeLabel(child, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Returns the full grade label for a child at the current date.
    /// </summary>
    public string GetCurrentGradeLabel(Child child, DateTime currentDate)
    {
        return GetGradeLabel(GetCurrentGrade(child, currentDate));
    }
}