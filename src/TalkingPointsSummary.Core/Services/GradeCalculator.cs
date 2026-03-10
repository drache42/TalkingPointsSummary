using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Calculates the current grade level for a child based on their starting grade,
/// starting year, and the current date. Students advance one grade every September 1st.
/// </summary>
public interface IGradeCalculator
{
    /// <summary>
    /// Calculates the child's current grade using the current UTC date.
    /// </summary>
    /// <param name="child">The child whose grade should be calculated.</param>
    int GetCurrentGrade(Child child);

    /// <summary>
    /// Calculates the child's grade for a specific date.
    /// </summary>
    /// <param name="child">The child whose grade should be calculated.</param>
    /// <param name="currentDate">The date to evaluate against the school-year rollover.</param>
    int GetCurrentGrade(Child child, DateTime currentDate);

    /// <summary>
    /// Calculates the current grade from the starting grade and school year using the current UTC date.
    /// </summary>
    /// <param name="startingGrade">The grade level at the starting school year.</param>
    /// <param name="startingYear">The school year in which the starting grade applies.</param>
    int GetCurrentGrade(int startingGrade, int startingYear);

    /// <summary>
    /// Calculates the grade for a specific date from the starting grade and school year.
    /// </summary>
    /// <param name="startingGrade">The grade level at the starting school year.</param>
    /// <param name="startingYear">The school year in which the starting grade applies.</param>
    /// <param name="currentDate">The date to evaluate against the school-year rollover.</param>
    int GetCurrentGrade(int startingGrade, int startingYear, DateTime currentDate);

    /// <summary>
    /// Returns the current school year using the current UTC date.
    /// </summary>
    int GetCurrentSchoolYear();

    /// <summary>
    /// Returns the school year for a specific date.
    /// </summary>
    /// <param name="currentDate">The date used to determine the active school year.</param>
    int GetCurrentSchoolYear(DateTime currentDate);

    /// <summary>
    /// Formats a numeric grade as a human-readable label.
    /// </summary>
    /// <param name="grade">The grade number to label.</param>
    string GetGradeLabel(int grade);

    /// <summary>
    /// Returns the child's current grade label using the current UTC date.
    /// </summary>
    /// <param name="child">The child whose grade label should be calculated.</param>
    string GetCurrentGradeLabel(Child child);

    /// <summary>
    /// Returns the child's grade label for a specific date.
    /// </summary>
    /// <param name="child">The child whose grade label should be calculated.</param>
    /// <param name="currentDate">The date used to determine the active school year.</param>
    string GetCurrentGradeLabel(Child child, DateTime currentDate);
}

/// <summary>
/// Default implementation of <see cref="IGradeCalculator"/> that advances grades each September 1st.
/// </summary>
/// <param name="timeProvider">Optional time provider used to supply the current date.</param>
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

    /// <summary>
    /// Returns the current school year using the configured time provider.
    /// </summary>
    public int GetCurrentSchoolYear()
    {
        return GetCurrentSchoolYear(_timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Returns the school year containing the supplied date.
    /// </summary>
    /// <param name="currentDate">The date used to determine the active school year.</param>
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