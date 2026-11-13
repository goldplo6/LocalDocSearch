namespace LocalDocSearch.Extractors;

/// <summary>Supported document regions only; comments, headers, notes and other unimplemented regions remain excluded.</summary>
public sealed record DocumentScopeOptions(
    bool WordBody = true,
    bool WordTables = true,
    bool ExcelHiddenSheets = false,
    bool PresentationText = true,
    bool PresentationTables = true);
