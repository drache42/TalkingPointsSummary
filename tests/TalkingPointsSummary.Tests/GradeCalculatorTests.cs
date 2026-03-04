using FluentAssertions;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class GradeCalculatorTests
{
    [Theory]
    [InlineData(0, 2025, "2026-03-01", 0)]   // Kindergarten in March 2026 (still 2025-2026 school year)
    [InlineData(3, 2025, "2026-03-01", 3)]   // 3rd grade in March 2026
    [InlineData(0, 2025, "2026-10-01", 1)]   // Advanced to 1st grade by Oct 2026 (2026-2027 school year)
    [InlineData(3, 2025, "2026-10-01", 4)]   // Advanced to 4th grade by Oct 2026
    [InlineData(0, 2025, "2025-09-01", 0)]   // Exact start: Sept 1, 2025 = Kindergarten
    [InlineData(0, 2025, "2025-08-31", 0)]   // Before school started (clamped to starting grade)
    [InlineData(5, 2023, "2026-03-01", 7)]   // Multiple years advanced: 2023→2024→2025 school year = +2 grades
    [InlineData(0, 2025, "2027-06-15", 1)]   // June 2027 = still in 2026-2027 school year = 1st grade
    public void GetCurrentGrade_ReturnsCorrectGrade(int startingGrade, int startingYear, string dateStr, int expectedGrade)
    {
        var date = DateTime.Parse(dateStr);
        var result = GradeCalculator.GetCurrentGrade(startingGrade, startingYear, date);
        result.Should().Be(expectedGrade);
    }

    [Theory]
    [InlineData(0, "Kindergarten")]
    [InlineData(1, "1st Grade")]
    [InlineData(2, "2nd Grade")]
    [InlineData(3, "3rd Grade")]
    [InlineData(4, "4th Grade")]
    [InlineData(5, "5th Grade")]
    [InlineData(12, "12th Grade")]
    public void GetGradeLabel_ReturnsCorrectLabel(int grade, string expectedLabel)
    {
        GradeCalculator.GetGradeLabel(grade).Should().Be(expectedLabel);
    }

    [Fact]
    public void GetCurrentGradeLabel_ClaraFroehlich_March2026_IsKindergarten()
    {
        var clara = new Child
        {
            Name = "Clara Froehlich",
            School = "James Baldwin Elementary",
            StartingGrade = 0,
            StartingYear = 2025
        };

        var result = GradeCalculator.GetCurrentGradeLabel(clara, new DateTime(2026, 3, 1));
        result.Should().Be("Kindergarten");
    }

    [Fact]
    public void GetCurrentGradeLabel_NolanFroehlich_March2026_Is3rdGrade()
    {
        var nolan = new Child
        {
            Name = "Nolan Froehlich",
            School = "Cascadia Elementary",
            StartingGrade = 3,
            StartingYear = 2025
        };

        var result = GradeCalculator.GetCurrentGradeLabel(nolan, new DateTime(2026, 3, 1));
        result.Should().Be("3rd Grade");
    }

    [Fact]
    public void GetCurrentGradeLabel_ClaraFroehlich_October2026_Is1stGrade()
    {
        var clara = new Child
        {
            Name = "Clara Froehlich",
            School = "James Baldwin Elementary",
            StartingGrade = 0,
            StartingYear = 2025
        };

        var result = GradeCalculator.GetCurrentGradeLabel(clara, new DateTime(2026, 10, 1));
        result.Should().Be("1st Grade");
    }

    [Fact]
    public void GetCurrentGradeLabel_NolanFroehlich_October2026_Is4thGrade()
    {
        var nolan = new Child
        {
            Name = "Nolan Froehlich",
            School = "Cascadia Elementary",
            StartingGrade = 3,
            StartingYear = 2025
        };

        var result = GradeCalculator.GetCurrentGradeLabel(nolan, new DateTime(2026, 10, 1));
        result.Should().Be("4th Grade");
    }
}
