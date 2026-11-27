// The board's records, the game log's rows and the pure readers (Starters, GameLogSplits,
// StatLineKeys) live in Core since the architecture review (§7); one import keeps every file reading them.
global using XLines.Core.Analytics;
global using XLines.Web.Components;
