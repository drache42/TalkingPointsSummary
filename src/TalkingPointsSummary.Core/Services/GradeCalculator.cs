using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Calculates the current grade level for a child based on their starting grade,
/// starting year, and the current date. Students advance one grade every September 1st.
/// </summary>
public static class GradeCalculator
{
    /// <summary>
    /// Returns the current grade as an integer (0 = Kindergarten).
    /// </summary>
    public static int GetCurrentGrade(Child child, DateTime currentDate)
    {
        return GetCurrentGrade(child.StartingGrade, child.StartingYear, currentDate);
    }

    /// <summary>
    /// Returns the current grade as an integer (0 = Kindergarten).
    /// </summary>
    public static int GetCurrentGrade(int startingGrade, int startingYear, DateTime currentDate)
    {
        int currentSchoolYear = GetCurrentSchoolYear(currentDate);
        int yearsAdvanced = currentSchoolYear - startingYear;
        return startingGrade + Math.Max(0, yearsAdvanced);
    }

    public static int GetCurrentSchoolYear(DateTime currentDate)
    {
        return currentDate.Month >= 9 ? currentDate.Year : currentDate.Year - 1;
    }

    /// <summary>
    /// Returns the grade label (e.g., "Kindergarten", "1st Grade", "2nd Grade").
    /// </summary>
    public static string GetGradeLabel(int grade)
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
    public static string GetCurrentGradeLabel(Child child, DateTime currentDate)
    {
        return GetGradeLabel(GetCurrentGrade(child, currentDate));
    }
}