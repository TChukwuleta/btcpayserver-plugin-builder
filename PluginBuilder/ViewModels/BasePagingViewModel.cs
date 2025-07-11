using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels;

public abstract class BasePagingViewModel
{
    public const int CountDefault = 50;
    public int Skip { get; set; } = 0;
    public int Count { get; set; } = CountDefault;
    public int? Total { get; set; } = null!;

    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string SearchTerm { get; set; } = null!;

    public int? TimezoneOffset { get; set; }
    public Dictionary<string, object> PaginationQuery { get; set; } = null!;
    public abstract int CurrentPageCount { get; }
}
