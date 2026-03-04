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
        // The starting year corresponds to the school year that begins in September.
        // e.g., startingYear=2025 means the 2025-2026 school year (Sept 2025 - Aug 2026).

        int currentSchoolYear;
        if (currentDate.Month >= 9)
        {
            // Sept-Dec: we're in the school year that started this calendar year
            currentSchoolYear = currentDate.Year;
        }
        else
        {
            // Jan-Aug: we're still in the school year that started last calendar year
            currentSchoolYear = currentDate.Year - 1;
        }

        int yearsAdvanced = currentSchoolYear - startingYear;
        return startingGrade + Math.Max(0, yearsAdvanced);
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
