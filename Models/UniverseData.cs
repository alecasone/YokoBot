using System.Text.Json.Serialization;

namespace Yoko.Bot.Models;

internal sealed class UniverseData
{
    public WorldDate? CurrentWorldDate { get; set; }
}

internal sealed class WorldDate
{
    public int Day { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }

    [JsonIgnore]
    public string Display => $"{Day:00}-{Month:00}-{Year:0000}";

    public bool IsValidDay(int day) =>
        Year is >= 1 and <= 9999 && Month is >= 1 and <= 12 &&
        day >= 1 && day <= DateTime.DaysInMonth(CalendarYear, Month);

    public WorldDate WithDay(int day) => new() { Day = day, Month = Month, Year = Year };

    private int CalendarYear => Year;
}
