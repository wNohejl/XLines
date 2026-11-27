using MudBlazor;

namespace XLines.Desk;

/// <summary>
/// Every icon the desk draws, by name. Each is MudBlazor's Material glyph today (ADR 0034 step 5
/// replaces the set), so an application names the Desk's icon and never the library's: changing
/// the glyphs then changes this file, not every window. Named after the Material glyph, which on
/// the desk is already the meaning (Refresh, Close, Search); an icon used for two things keeps the
/// glyph's name rather than one of them.
/// </summary>
public static class DeskIcons
{
    public const string Add = Icons.Material.Filled.Add;
    public const string AddBox = Icons.Material.Filled.AddBox;
    public const string ArrowBack = Icons.Material.Filled.ArrowBack;
    public const string ArrowOutward = Icons.Material.Filled.ArrowOutward;
    public const string ArrowRight = Icons.Material.Filled.ArrowRight;
    public const string Bolt = Icons.Material.Filled.Bolt;
    public const string Bookmark = Icons.Material.Filled.Bookmark;
    public const string BookmarkAdd = Icons.Material.Filled.BookmarkAdd;
    public const string CalendarMonth = Icons.Material.Filled.CalendarMonth;
    public const string Cancel = Icons.Material.Filled.Cancel;
    public const string Check = Icons.Material.Filled.Check;
    public const string CheckCircle = Icons.Material.Filled.CheckCircle;
    public const string ChevronLeft = Icons.Material.Filled.ChevronLeft;
    public const string ChevronRight = Icons.Material.Filled.ChevronRight;
    public const string ClearAll = Icons.Material.Filled.ClearAll;
    public const string Close = Icons.Material.Filled.Close;
    public const string CloudDownload = Icons.Material.Filled.CloudDownload;
    public const string CompareArrows = Icons.Material.Filled.CompareArrows;
    public const string ContentCopy = Icons.Material.Filled.ContentCopy;
    public const string CropSquare = Icons.Material.Filled.CropSquare;
    public const string Dashboard = Icons.Material.Filled.Dashboard;
    public const string DateRange = Icons.Material.Filled.DateRange;
    public const string Delete = Icons.Material.Filled.Delete;
    public const string DeleteOutline = Icons.Material.Filled.DeleteOutline;
    public const string Description = Icons.Material.Filled.Description;
    public const string East = Icons.Material.Filled.East;
    public const string Edit = Icons.Material.Filled.Edit;
    public const string Error = Icons.Material.Filled.Error;
    public const string Event = Icons.Material.Filled.Event;
    public const string ExpandLess = Icons.Material.Filled.ExpandLess;
    public const string ExpandMore = Icons.Material.Filled.ExpandMore;
    public const string FilterAltOff = Icons.Material.Filled.FilterAltOff;
    public const string FilterNone = Icons.Material.Filled.FilterNone;
    public const string Gavel = Icons.Material.Filled.Gavel;
    public const string Groups = Icons.Material.Filled.Groups;
    public const string History = Icons.Material.Filled.History;
    public const string Info = Icons.Material.Filled.Info;
    public const string Key = Icons.Material.Filled.Key;
    public const string Leaderboard = Icons.Material.Filled.Leaderboard;
    public const string Link = Icons.Material.Filled.Link;
    public const string Login = Icons.Material.Filled.Login;
    public const string Logout = Icons.Material.Filled.Logout;
    public const string MenuBook = Icons.Material.Filled.MenuBook;
    public const string MonitorHeart = Icons.Material.Filled.MonitorHeart;
    public const string MoreHoriz = Icons.Material.Filled.MoreHoriz;
    public const string Notes = Icons.Material.Filled.Notes;
    public const string OpenInNew = Icons.Material.Filled.OpenInNew;
    public const string Person = Icons.Material.Filled.Person;
    public const string PersonSearch = Icons.Material.Filled.PersonSearch;
    public const string PlayArrow = Icons.Material.Filled.PlayArrow;
    public const string PlaylistAdd = Icons.Material.Filled.PlaylistAdd;
    public const string QueryStats = Icons.Material.Filled.QueryStats;
    public const string Receipt = Icons.Material.Filled.Receipt;
    public const string Refresh = Icons.Material.Filled.Refresh;
    public const string Remove = Icons.Material.Filled.Remove;
    public const string Replay = Icons.Material.Filled.Replay;
    public const string ReportProblem = Icons.Material.Filled.ReportProblem;
    public const string Restore = Icons.Material.Filled.Restore;
    public const string Schedule = Icons.Material.Filled.Schedule;
    public const string Scoreboard = Icons.Material.Filled.Scoreboard;
    public const string Search = Icons.Material.Filled.Search;
    public const string Shield = Icons.Material.Filled.Shield;
    public const string ShowChart = Icons.Material.Filled.ShowChart;
    public const string SportsFootball = Icons.Material.Filled.SportsFootball;
    public const string SportsScore = Icons.Material.Filled.SportsScore;
    public const string Stop = Icons.Material.Filled.Stop;
    public const string Sync = Icons.Material.Filled.Sync;
    public const string TaskAlt = Icons.Material.Filled.TaskAlt;
    public const string Timeline = Icons.Material.Filled.Timeline;
    public const string Tune = Icons.Material.Filled.Tune;
    public const string Undo = Icons.Material.Filled.Undo;
    public const string UnfoldMore = Icons.Material.Filled.UnfoldMore;
    public const string VerticalAlignBottom = Icons.Material.Filled.VerticalAlignBottom;
    public const string ViewColumn = Icons.Material.Filled.ViewColumn;
    public const string ViewList = Icons.Material.Filled.ViewList;
    public const string Warning = Icons.Material.Filled.Warning;
    public const string West = Icons.Material.Filled.West;
    public const string Widgets = Icons.Material.Filled.Widgets;
}
