﻿using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using KustoLoco.Core.Settings;
using Lokql.Engine;
using NotNullStrings;
using FontFamily = System.Windows.Media.FontFamily;

namespace lokqlDx;

/// <summary>
///     Simple text editor for running queries
/// </summary>
/// <remarks>
///     The key thing this provides is an event based on SHIFT-ENTER that
///     selects text around the cursor and sends it to the RunEvent
/// </remarks>
public partial class QueryEditor : UserControl
{
    private readonly EditorHelper _editorHelper;
    private readonly SchemaIntellisenseProvider _schemaIntellisenseProvider = new();

    private CompletionWindow? _completionWindow;

    private IntellisenseEntry[] _internalCommands = [];
    private bool _isBusy;
    private IntellisenseEntry[] _kqlFunctionEntries = [];


    private IntellisenseEntry[] _settingNames = [];

    public IntellisenseEntry[] KqlOperatorEntries = [];

    public QueryEditor()
    {
        InitializeComponent();
        Query.TextArea.TextEntering += textEditor_TextArea_TextEntering;
        Query.TextArea.TextEntered += textEditor_TextArea_TextEntered;
        _editorHelper = new EditorHelper(Query);
    }

    #region public interface

    public event EventHandler<QueryEditorRunEventArgs>? RunEvent;

    #endregion


    /// <summary>
    ///     searches for lines around the cursor that contain text
    /// </summary>
    /// <remarks>
    ///     This allows us to easily run multi-line queries
    /// </remarks>
    private string GetTextAroundCursor()
    {
        if (Query.SelectionLength > 0) return Query.SelectedText.Trim();

        var i = _editorHelper.LineAtCaret().LineNumber;

        var sb = new StringBuilder();

        while (i > 1 && _editorHelper.TextInLine(i - 1).Trim().Length > 0)
            i--;
        while (i <= Query.LineCount && _editorHelper.TextInLine(i).Trim().Length > 0)
        {
            sb.AppendLine(_editorHelper.TextInLine(i));
            i++;
        }

        return sb.ToString().Trim();
    }

    public void SetFontSize(double newSize)
    {
        Query.FontSize = newSize;
    }

    public void SetText(string text)
    {
        Query.Text = text;
    }

    public void SetFont(string font)
    {
        Query.FontFamily = new FontFamily(font);
    }

    public string GetText()
    {
        return Query.Text;
    }

    public void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        BusyStatus.Visibility = isBusy ? Visibility.Visible : Visibility.Hidden;
        Query.IsReadOnly = isBusy;
    }

    private void TextBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_isBusy)
        {
            e.Handled = false;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Handled = true;
    }

    private void InsertAtCursor(string text)
    {
        Query.Document.Insert(Query.CaretOffset, text);
    }

    private void TextBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);

            var newString = files.Select(f => $".{VerbFromExtension(f)} \"{f}\"").JoinAsLines();
            InsertAtCursor(newString);
        }

        e.Handled = true;

        string VerbFromExtension(string f)
        {
            return f.EndsWith(".csl")
                ? "run"
                : "load";
        }
    }

    private void Query_OnPreviewDragEnter(object sender, DragEventArgs drgevent)
    {
        drgevent.Handled = true;


        // Check that the data being dragged is a file
        if (drgevent.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // Get an array with the filenames of the files being dragged
            var files = (string[])drgevent.Data.GetData(DataFormats.FileDrop);

            drgevent.Effects = files.Length > 0 ? DragDropEffects.Move : DragDropEffects.None;
        }
        else
        {
            drgevent.Effects = DragDropEffects.None;
        }
    }

    public void SetWordWrap(bool wordWrap)
    {
        Query.WordWrap = wordWrap;
    }

    public void ShowLineNumbers(bool show)
    {
        Query.ShowLineNumbers = show;
    }

    private void InternalEditor_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            if (Keyboard.IsKeyDown(Key.RightShift) || Keyboard.IsKeyDown(Key.LeftShift))
            {
                e.Handled = true;
                var query = GetTextAroundCursor();
                if (query.Length > 0) RunEvent?.Invoke(this, new QueryEditorRunEventArgs(query));
            }
    }

    /// <summary>
    ///     Gets a resource name independent of namespace
    /// </summary>
    /// <remarks>
    ///     For some reason dotnet publish decides to lower-case the
    ///     namespace in the resource name. In any case, we really don't want to trust
    ///     that the namespace won't change so do a match against the filename
    /// </remarks>
    private Stream SafeGetResourceStream(string substring)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var availableResources = assembly.GetManifestResourceNames();
        var wanted =
            availableResources.Single(name => name.Contains(substring, StringComparison.CurrentCultureIgnoreCase));
        return assembly.GetManifestResourceStream(wanted)!;
    }

    private void QueryEditor_OnLoaded(object sender, RoutedEventArgs e)
    {
        using var s = SafeGetResourceStream("SyntaxHighlighting.xml");
        using var reader = new XmlTextReader(s);
        Query.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        using var functions = SafeGetResourceStream("IntellisenseFunctions.json");
        _kqlFunctionEntries = JsonSerializer.Deserialize<IntellisenseEntry[]>(functions!)!;
        using var ops = SafeGetResourceStream("IntellisenseOperators.json");
        KqlOperatorEntries = JsonSerializer.Deserialize<IntellisenseEntry[]>(ops!)!;
    }

    private void ShowCompletions(IEnumerable<IntellisenseEntry> completions, string prefix, int rewind)
    {
        if (!completions.Any())
            return;

        _completionWindow = new CompletionWindow(Query.TextArea)
        {
            CloseWhenCaretAtBeginning = true
        };
        IList<ICompletionData> data = _completionWindow.CompletionList.CompletionData;
        foreach (var k in completions.OrderBy(k => k.Name))
            data.Add(new MyCompletionData(k, prefix, rewind));
        _completionWindow.Show();
        _completionWindow.Closed += delegate { _completionWindow = null; };
    }

    private void textEditor_TextArea_TextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_completionWindow != null && !_completionWindow.CompletionList.ListBox.HasItems)
        {
            _completionWindow.Close();
            return;
        }

        if (e.Text == ".")
        {
            //only show completions if we are at the start of a line
            var textToLeft = _editorHelper.TextToLeftOfCaret();
            if (textToLeft.TrimStart() == ".") ShowCompletions(_internalCommands, string.Empty, 0);
            return;
        }

        if (e.Text == "|")
        {
            ShowCompletions(KqlOperatorEntries, " ", 0);
            return;
        }

        if (e.Text == "@")
        {
            var blockText = GetTextAroundCursor();
            var columns = _schemaIntellisenseProvider.GetColumns(blockText);
            ShowCompletions(columns, string.Empty, 1);
            return;
        }

        if (e.Text == "[")
        {
            var blockText = GetTextAroundCursor();
            var tables = _schemaIntellisenseProvider.GetTables(blockText);
            ShowCompletions(tables, string.Empty, 1);
            return;
        }

        if (e.Text == "$")
        {
            ShowCompletions(_settingNames, string.Empty, 0);
            return;
        }

        if (e.Text == "?") ShowCompletions(_kqlFunctionEntries, string.Empty, 1);
    }

    public void AddInternalCommands(IntellisenseEntry[] verbs)
    {
        _internalCommands = verbs;
    }

    private void textEditor_TextArea_TextEntering(object sender, TextCompositionEventArgs e)
    {
        bool CharContinuesAutoComplete(char c) => (char.IsLetterOrDigit(c) || c == '_' || c == '.');
        if (e.Text.Length > 0 && _completionWindow != null)
        {
            // Whenever a non-letter is typed while the completion window is open,
            // insert the currently selected element.
            if (!CharContinuesAutoComplete(e.Text[0]))
                _completionWindow.CompletionList.RequestInsertion(e);
        }
        // Do not set e.Handled=true.
        // We still want to insert the character that was typed.
    }

    public void AddSettingsForIntellisense(KustoSettingsProvider settings)
    {
        _settingNames = settings.Enumerate()
            .Select(s => new IntellisenseEntry(s.Name, s.Value, string.Empty))
            .ToArray();
    }

    public void SetSchema(SchemaLine[] getSchema)
    {
        _schemaIntellisenseProvider.SetSchema(getSchema);
    }
}

public class QueryEditorRunEventArgs(string query) : EventArgs
{
    public string Query { get; private set; } = query;
}

/// Implements AvalonEdit ICompletionData interface to provide the entries in the
/// completion drop down.
public class MyCompletionData(IntellisenseEntry entry, string prefix, int rewind) : ICompletionData
{
    public ImageSource? Image => null;

    public string Text => entry.Name;

    // Use this property if you want to show a fancy UIElement in the list.
    public object Content => Text;

    public object Description
        => entry.Syntax.IsBlank()
            ? entry.Description
            : $@"{entry.Description}
Usage: {entry.Syntax}";


    public double Priority => 1.0;

    public void Complete(TextArea textArea, ISegment completionSegment,
        EventArgs insertionRequestEventArgs)
    {
        var seg = new TextSegment
        {
            StartOffset = completionSegment.Offset - rewind,
            Length = completionSegment.Length + rewind
        };
        textArea.Document.Replace(seg, prefix + Text);
    }
}

public class EditorHelper(TextEditor query)
{
    public TextEditor Query { get; set; } = query;


    public string GetText(DocumentLine line)
    {
        return Query.Document.GetText(line.Offset, line.Length);
    }

    public string TextInLine(int line)
    {
        return GetText(Query.Document.GetLineByNumber(line));
    }

    public DocumentLine LineAtCaret()
    {
        return Query.Document.GetLineByOffset(Query.CaretOffset);
    }

    public string TextToLeftOfCaret()
    {
        var line = LineAtCaret();
        return Query.Document.GetText(line.Offset, Query.CaretOffset - line.Offset);
    }
}

public readonly record struct IntellisenseEntry(string Name, string Description, string Syntax);

public class SchemaIntellisenseProvider
{
    private readonly SchemaLine[] _dynamicSchema = [];
    private SchemaLine[] _schemaLines = [];

    private IEnumerable<SchemaLine> AllSchemaLines()
    {
        return _schemaLines.Concat(_dynamicSchema);
    }


    private string[] TablesForCommand(string command)
    {
        return AllSchemaLines().Where(s => s.Command == command)
            .Select(s => s.Table)
            .Distinct()
            .ToArray();
    }

    private string[] AllCommands()
    {
        //it's important to order by length so that the longest commands
        //are matched first since the "dynamic" command have an empty string
        //as the command
        return AllSchemaLines().Select(s => s.Command).Distinct()
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    public IntellisenseEntry[] GetTables(string blockText)
    {
        foreach (var command in AllCommands())
            if (blockText.Contains(command))
                return TablesForCommand(command)
                    .Select(t => new IntellisenseEntry(t, $"{command} table", ""))
                    .ToArray();

        //no command found so return empty
        return [];
    }

    public IntellisenseEntry[] GetColumns(string blockText)
    {
        foreach (var command in AllCommands())
            if (blockText.Contains(command))
            {
                var tables = TablesForCommand(command);
                //TODO this is a bit fuzzy since short table names could be substrings
                //of longer ones or even keywords.  We should probably use a regex
                var matchingTables = tables.Where(blockText.Contains).ToArray();
                var columns = AllSchemaLines().Where(s => s.Command == command)
                    .Where(s => matchingTables.Contains(s.Table))
                    .Select(c => new IntellisenseEntry(c.Column, $"{c.Command} column for {c.Table}", ""))
                    .ToArray();
                return columns;
            }

        //no command found so return empty
        return [];
    }

    public void SetSchema(SchemaLine[] schema)
    {
        _schemaLines = schema;
    }
}
