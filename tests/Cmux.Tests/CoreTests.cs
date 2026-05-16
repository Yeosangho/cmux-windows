using System.Reflection;
using System.Text;
using System.Text.Json;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class VtParserTests
{
    [Fact]
    public void Feed_PrintableCharacters_RaisesOnPrint()
    {
        var parser = new VtParser();
        var printed = new List<char>();
        parser.OnPrint = c => printed.Add(c);

        parser.Feed("Hello");

        printed.Should().Equal('H', 'e', 'l', 'l', 'o');
    }

    [Fact]
    public void Feed_C0Controls_RaisesOnExecute()
    {
        var parser = new VtParser();
        var executed = new List<byte>();
        parser.OnExecute = b => executed.Add(b);

        parser.Feed("\r\n");

        executed.Should().Contain(0x0D); // CR
        executed.Should().Contain(0x0A); // LF
    }

    [Fact]
    public void Feed_CsiSequence_RaisesOnCsiDispatch()
    {
        var parser = new VtParser();
        List<int>? receivedParams = null;
        char receivedFinal = '\0';
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedParams = new List<int>(parameters);
            receivedFinal = final;
        };

        // CSI 10;20H = cursor position (row 10, col 20)
        parser.Feed("\x1b[10;20H");

        receivedFinal.Should().Be('H');
        receivedParams.Should().NotBeNull();
        receivedParams.Should().Equal(10, 20);
    }

    [Fact]
    public void Feed_SgrReset_RaisesOnCsiDispatch()
    {
        var parser = new VtParser();
        char receivedFinal = '\0';
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedFinal = final;
        };

        parser.Feed("\x1b[0m");

        receivedFinal.Should().Be('m');
    }

    [Fact]
    public void Feed_OscString_RaisesOnOscDispatch()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        // OSC 0 ; My Title BEL
        parser.Feed("\x1b]0;My Title\x07");

        receivedOsc.Should().Be("0;My Title");
    }

    [Fact]
    public void Feed_Osc9Notification_Detected()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]9;Agent needs input\x07");

        receivedOsc.Should().Be("9;Agent needs input");
    }

    [Fact]
    public void Feed_Osc777Notification_Detected()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]777;notify;Claude;Waiting for input\x07");

        receivedOsc.Should().Be("777;notify;Claude;Waiting for input");
    }

    [Fact]
    public void Feed_OscWith0x9CInUtf8Body_DoesNotTruncate()
    {
        // Regression: 0x9C is a valid UTF-8 continuation byte that appears in
        // many CJK glyphs (e.g. "시" U+C2DC encodes to EC 8B 9C). Earlier
        // versions of the parser honored 0x9C as 8-bit ST and truncated the
        // OSC payload mid-character, producing U+FFFD on decode and a toast
        // body like "메시지" → "메�".
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]99;t=Claude;b=테스트 메시지\x07");

        receivedOsc.Should().Be("99;t=Claude;b=테스트 메시지");
        receivedOsc.Should().NotContain("�");
    }

    [Fact]
    public void Feed_EscSequence_RaisesOnEscDispatch()
    {
        var parser = new VtParser();
        byte? dispatched = null;
        parser.OnEscDispatch = b => dispatched = b;

        // ESC 7 = DECSC (save cursor)
        parser.Feed("\u001b7");

        dispatched.Should().Be((byte)'7');
    }

    [Fact]
    public void Feed_PrivateModeSet_ParsesCorrectly()
    {
        var parser = new VtParser();
        string? receivedQualifier = null;
        List<int>? receivedParams = null;
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedParams = new List<int>(parameters);
            receivedQualifier = qualifier;
        };

        // CSI ? 25 h = show cursor (DECTCEM)
        parser.Feed("\x1b[?25h");

        receivedParams.Should().Equal(25);
        receivedQualifier.Should().Contain("?");
    }
}

public class TerminalBufferTests
{
    [Fact]
    public void WriteChar_AdvancesCursor()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('A');

        buffer.CursorCol.Should().Be(1);
        buffer.CellAt(0, 0).Character.Should().Be('A');
    }

    [Fact]
    public void LineFeed_AtBottom_ScrollsUp()
    {
        var buffer = new TerminalBuffer(80, 3);

        buffer.WriteString("Line1");
        buffer.NewLine();
        buffer.WriteString("Line2");
        buffer.NewLine();
        buffer.WriteString("Line3");
        buffer.NewLine(); // Should scroll

        buffer.ScrollbackCount.Should().Be(1);
    }

    [Fact]
    public void EraseInDisplay_Mode2_ClearsAll()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("Hello World");

        buffer.EraseInDisplay(2);

        buffer.CellAt(0, 0).Character.Should().Be(' ');
    }

    [Fact]
    public void Resize_PreservesContent()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("ABC");

        buffer.Resize(40, 12);

        buffer.CellAt(0, 0).Character.Should().Be('A');
        buffer.CellAt(0, 1).Character.Should().Be('B');
        buffer.CellAt(0, 2).Character.Should().Be('C');
        buffer.Cols.Should().Be(40);
        buffer.Rows.Should().Be(12);
    }

    [Fact]
    public void ScrollRegion_ScrollsOnlyWithinRegion()
    {
        var buffer = new TerminalBuffer(10, 5);
        buffer.SetScrollRegion(1, 3);
        buffer.MoveCursorTo(3, 0); // Bottom of scroll region
        buffer.WriteString("X");
        buffer.LineFeed(); // Should scroll only lines 1-3

        buffer.CellAt(0, 0).Character.Should().Be(' '); // Line 0 untouched
    }

    [Fact]
    public void SaveRestore_CursorPosition()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MoveCursorTo(5, 10);
        buffer.SaveCursor();

        buffer.MoveCursorTo(0, 0);
        buffer.RestoreCursor();

        buffer.CursorRow.Should().Be(5);
        buffer.CursorCol.Should().Be(10);
    }
}

public class OscHandlerTests
{
    [Fact]
    public void Handle_Osc0_ChangesTitleEvent()
    {
        var handler = new OscHandler();
        string? title = null;
        handler.TitleChanged += t => title = t;

        handler.Handle("0;My Terminal Title");

        title.Should().Be("My Terminal Title");
    }

    [Fact]
    public void Handle_Osc7_ChangesWorkingDirectory()
    {
        var handler = new OscHandler();
        string? dir = null;
        handler.WorkingDirectoryChanged += d => dir = d;

        handler.Handle("7;file://localhost/C:/Users/test/project");

        dir.Should().NotBeNull();
    }

    [Fact]
    public void Handle_Osc9_FiresNotification()
    {
        var handler = new OscHandler();
        string? body = null;
        handler.NotificationReceived += (t, s, b, id, ts) => body = b;

        handler.Handle("9;Agent is waiting for your input");

        body.Should().Be("Agent is waiting for your input");
    }

    [Fact]
    public void Handle_Osc99_KeyValue_ParsesCorrectly()
    {
        var handler = new OscHandler();
        string? title = null, body = null;
        handler.NotificationReceived += (t, s, b, id, ts) => { title = t; body = b; };

        handler.Handle("99;t=Claude Code;b=Waiting for input");

        title.Should().Be("Claude Code");
        body.Should().Be("Waiting for input");
    }

    [Fact]
    public void Handle_Osc99_KeyValue_ParsesIdAndTimestamp()
    {
        var handler = new OscHandler();
        string? capturedId = null;
        DateTime? capturedTs = null;
        handler.NotificationReceived += (t, s, b, id, ts) => { capturedId = id; capturedTs = ts; };

        handler.Handle("99;t=Claude;b=done;i=run-42;ts=1700000000");

        capturedId.Should().Be("run-42");
        capturedTs.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
    }

    [Fact]
    public void Handle_Osc777_Notify_ParsesCorrectly()
    {
        var handler = new OscHandler();
        string? title = null, body = null;
        handler.NotificationReceived += (t, s, b, id, ts) => { title = t; body = b; };

        handler.Handle("777;notify;Claude;Task completed");

        title.Should().Be("Claude");
        body.Should().Be("Task completed");
    }

    [Fact]
    public void Handle_Osc133_FiresPromptMarker()
    {
        var handler = new OscHandler();
        char? marker = null;
        handler.ShellPromptMarker += (m, payload) => marker = m;

        handler.Handle("133;A");

        marker.Should().Be('A');
    }
}

public class SplitNodeTests
{
    [Fact]
    public void CreateLeaf_IsLeaf()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.IsLeaf.Should().BeTrue();
        node.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void Split_TurnsLeafIntoContainer()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");

        var newChild = node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        node.IsLeaf.Should().BeFalse();
        node.First.Should().NotBeNull();
        node.Second.Should().NotBeNull();
        node.First!.PaneId.Should().Be("pane-1");
        newChild.PaneId.Should().NotBeNull();
    }

    [Fact]
    public void Split_NonLeaf_ThrowsInvalidOperation()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var act = () => node.Split(Cmux.Core.Models.SplitDirection.Horizontal);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FindNode_FindsLeaf()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var found = node.FindNode("pane-1");

        found.Should().NotBeNull();
        found!.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void GetLeaves_ReturnsAllLeaves()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var leaves = node.GetLeaves().ToList();

        leaves.Should().HaveCount(2);
        leaves[0].PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void Remove_CollapsesParent()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        var newChild = node.Split(Cmux.Core.Models.SplitDirection.Vertical);
        var newPaneId = newChild.PaneId!;

        bool removed = node.Remove(newPaneId);

        removed.Should().BeTrue();
        node.IsLeaf.Should().BeTrue();
        node.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void GetNextLeaf_CyclesCorrectly()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        var child2 = node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var next = node.GetNextLeaf("pane-1");
        next.Should().NotBeNull();
        next!.PaneId.Should().Be(child2.PaneId);

        // Wraps around
        var wrap = node.GetNextLeaf(child2.PaneId!);
        wrap.Should().NotBeNull();
        wrap!.PaneId.Should().Be("pane-1");
    }
}

public class TerminalColorTests
{
    [Fact]
    public void FromIndex_BasicColors_ReturnsExpected()
    {
        var black = TerminalColor.FromIndex(0);
        black.R.Should().Be(0);
        black.G.Should().Be(0);
        black.B.Should().Be(0);

        var white = TerminalColor.FromIndex(15);
        white.R.Should().Be(0xFF);
        white.G.Should().Be(0xFF);
        white.B.Should().Be(0xFF);
    }

    [Fact]
    public void FromIndex_256Colors_DoesNotThrow()
    {
        for (int i = 0; i < 256; i++)
        {
            var act = () => TerminalColor.FromIndex(i);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void FromRgb_StoresCorrectValues()
    {
        var color = TerminalColor.FromRgb(0x12, 0x34, 0x56);
        color.R.Should().Be(0x12);
        color.G.Should().Be(0x34);
        color.B.Should().Be(0x56);
        color.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Default_IsMarkedAsDefault()
    {
        var def = TerminalColor.Default;
        def.IsDefault.Should().BeTrue();
    }
}

public class TerminalSelectionTests
{
    [Fact]
    public void StartAndExtend_CreatesSelection()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 5);
        selection.ExtendSelection(0, 10);

        selection.HasSelection.Should().BeTrue();
        selection.IsSelected(0, 7).Should().BeTrue();
        selection.IsSelected(0, 12).Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesSelection()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 10);

        selection.ClearSelection();

        selection.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void GetSelectedText_ExtractsCorrectly()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("Hello World");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 4);

        var text = selection.GetSelectedText(buffer);
        text.Should().Be("Hello");
    }

    [Fact]
    public void IsSelected_MultiLine_Works()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 5);
        selection.ExtendSelection(2, 10);

        selection.IsSelected(0, 6).Should().BeTrue();
        selection.IsSelected(1, 0).Should().BeTrue(); // Middle line, full
        selection.IsSelected(2, 5).Should().BeTrue();
        selection.IsSelected(2, 11).Should().BeFalse();
    }

    [Fact]
    public void GetSelectedText_SoftWrap_NoNewlineInserted()
    {
        // Soft-wrap detection: when a row fills the full terminal width
        // with non-space content, the next row is treated as a wrapped
        // continuation. Copy from Claude Code TUI / wrapped prose should
        // round-trip back into a single logical line.
        var buffer = new TerminalBuffer(10, 2);
        buffer.WriteString("AAAAAAAAAABBBBB"); // 10 A's wrap; 5 B's on row 1

        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(1, 4);

        var text = selection.GetSelectedText(buffer);
        text.Should().Be("AAAAAAAAAABBBBB"); // no \n inserted at the wrap
    }

    [Fact]
    public void GetSelectedText_HardNewline_NewlineInserted()
    {
        // Hard newline: row 0 does NOT fill the full width (trailing
        // padding remains) → the original source emitted a real newline.
        // Copy must preserve it as `\n`.
        var buffer = new TerminalBuffer(10, 2);
        buffer.WriteString("AAAA");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("BBBB");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(1, 3);

        var text = selection.GetSelectedText(buffer);
        text.Should().Be("AAAA" + System.Environment.NewLine + "BBBB"); // \n inserted between rows
    }
}


public class AlternateScreenBufferTests
{
    [Fact]
    public void SwitchToAlternateScreen_ClearsAndSavesMainBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');
        buffer.CursorCol.Should().Be(1);

        buffer.SwitchToAlternateScreen();

        buffer.IsAlternateScreen.Should().BeTrue();
        buffer.CursorRow.Should().Be(0);
        buffer.CursorCol.Should().Be(0);
        buffer.CellAt(0, 0).Character.Should().Be(' ');
    }

    [Fact]
    public void SwitchToMainScreen_RestoresPreviousState()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('A');
        buffer.WriteChar('B');
        int savedCol = buffer.CursorCol;

        buffer.SwitchToAlternateScreen();
        buffer.WriteChar('Z');

        buffer.SwitchToMainScreen();

        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.CursorCol.Should().Be(savedCol);
        buffer.CellAt(0, 0).Character.Should().Be('A');
        buffer.CellAt(0, 1).Character.Should().Be('B');
    }

    [Fact]
    public void SwitchToAlternateScreen_DoubleSwitchIsNoop()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');

        buffer.SwitchToAlternateScreen();
        buffer.WriteChar('Y');

        buffer.SwitchToAlternateScreen();

        buffer.CellAt(0, 0).Character.Should().Be('Y');
    }

    [Fact]
    public void SwitchToMainScreen_WhenNotAlternate_IsNoop()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');

        buffer.SwitchToMainScreen();

        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.CellAt(0, 0).Character.Should().Be('X');
    }
}

public class TerminalModeTests
{
    [Fact]
    public void ApplicationCursorKeys_DefaultsToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.ApplicationCursorKeys.Should().BeFalse();
    }

    [Fact]
    public void BracketedPasteMode_DefaultsToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.BracketedPasteMode.Should().BeFalse();
    }

    [Fact]
    public void ApplicationCursorKeys_CanBeSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.ApplicationCursorKeys = true;
        buffer.ApplicationCursorKeys.Should().BeTrue();
    }

    [Fact]
    public void BracketedPasteMode_CanBeSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.BracketedPasteMode = true;
        buffer.BracketedPasteMode.Should().BeTrue();
    }
}

public class UrlDetectorTests
{
    [Fact]
    public void FindUrls_DetectsHttps()
    {
        var urls = UrlDetector.FindUrls("Visit https://example.com/path for info");
        urls.Should().HaveCount(1);
        urls[0].url.Should().Be("https://example.com/path");
        urls[0].startCol.Should().Be(6);
    }

    [Fact]
    public void FindUrls_DetectsMultipleUrls()
    {
        var urls = UrlDetector.FindUrls("Go to http://a.com and https://b.io/x");
        urls.Should().HaveCount(2);
    }

    [Fact]
    public void FindUrls_NoUrlsReturnsEmpty()
    {
        var urls = UrlDetector.FindUrls("No urls here just text");
        urls.Should().BeEmpty();
    }

    [Fact]
    public void GetRowText_ExtractsBufferRow()
    {
        var buffer = new TerminalBuffer(10, 1);
        buffer.WriteChar('H');
        buffer.WriteChar('i');
        var text = UrlDetector.GetRowText(buffer, 0);
        text.Should().StartWith("Hi");
        text.Should().HaveLength(10);
    }
}

public class MouseModeTests
{
    [Fact]
    public void MouseTrackingModes_DefaultToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MouseTrackingNormal.Should().BeFalse();
        buffer.MouseTrackingButton.Should().BeFalse();
        buffer.MouseTrackingAny.Should().BeFalse();
        buffer.MouseSgrExtended.Should().BeFalse();
        buffer.MouseEnabled.Should().BeFalse();
    }

    [Fact]
    public void MouseEnabled_TrueWhenAnyTrackingSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MouseTrackingNormal = true;
        buffer.MouseEnabled.Should().BeTrue();
    }

    [Fact]
    public void MouseEnabled_TrueWhenButtonTrackingSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MouseTrackingButton = true;
        buffer.MouseEnabled.Should().BeTrue();
    }
}

public class AgentConversationStoreMessageParsingTests
{
    private static readonly MethodInfo ReadMessagesMethod = typeof(AgentConversationStoreService)
        .GetMethod("ReadMessagesFromFile", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly JsonSerializerOptions CamelCaseIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CamelCaseCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [Fact]
    public void ReadMessagesFromFile_ParsesMultilineObjects_WithUtf8Bom()
    {
        var message1 = new AgentConversationMessage
        {
            Id = "m1",
            ThreadId = "t1",
            Role = "user",
            Content = "hello",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 0, 0, DateTimeKind.Utc),
        };
        var message2 = new AgentConversationMessage
        {
            Id = "m2",
            ThreadId = "t1",
            Role = "assistant",
            Content = "hi",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 0, 5, DateTimeKind.Utc),
        };

        var json = string.Join(
            Environment.NewLine,
            JsonSerializer.Serialize(message1, CamelCaseIndented),
            JsonSerializer.Serialize(message2, CamelCaseIndented)) + Environment.NewLine;

        var path = Path.Combine(Path.GetTempPath(), $"cmux-agent-{Guid.NewGuid():N}.jsonl");
        try
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();
            File.WriteAllBytes(path, bytes);

            var output = new List<AgentConversationMessage>();
            ReadMessagesMethod.Invoke(null, [path, output]);

            output.Should().HaveCount(2);
            output[0].Id.Should().Be("m1");
            output[1].Id.Should().Be("m2");
            output[0].Content.Should().Be("hello");
            output[1].Content.Should().Be("hi");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadMessagesFromFile_FallbackLineParser_HandlesBomOnFirstLine()
    {
        var message1 = new AgentConversationMessage
        {
            Id = "line1",
            ThreadId = "t2",
            Role = "user",
            Content = "first",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 1, 0, DateTimeKind.Utc),
        };
        var message2 = new AgentConversationMessage
        {
            Id = "line2",
            ThreadId = "t2",
            Role = "assistant",
            Content = "second",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 1, 5, DateTimeKind.Utc),
        };

        var line1 = JsonSerializer.Serialize(message1, CamelCaseCompact);
        var line2 = JsonSerializer.Serialize(message2, CamelCaseCompact);
        var malformed = "{\"broken\": }";
        var content = string.Join(Environment.NewLine, line1, malformed, line2) + Environment.NewLine;

        var path = Path.Combine(Path.GetTempPath(), $"cmux-agent-{Guid.NewGuid():N}.jsonl");
        try
        {
            var payload = Encoding.UTF8.GetBytes(content);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();
            File.WriteAllBytes(path, bytes);

            var output = new List<AgentConversationMessage>();
            ReadMessagesMethod.Invoke(null, [path, output]);

            output.Should().HaveCount(2);
            output.Select(m => m.Id).Should().ContainInOrder("line1", "line2");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

/// <summary>
/// Regression coverage for the OSC 1338 cmux agent announce path. These
/// tests pin down the wire format used by `cmux-announce.sh` /
/// `cmux-announce.ps1` so a future change to either side notices the
/// drift before users hit "Claude SSH pane isn't classified" symptoms.
/// </summary>
public class OscHandlerAgentAnnounceTests
{
    [Fact]
    public void Osc1338_ClaudeStartAnnounce_FiresAgentAnnounceReceived()
    {
        var handler = new OscHandler();
        string? agent = null;
        string? ev = null;
        string? host = null;
        string? sid = null;
        DateTime? ts = null;
        handler.AgentAnnounceReceived += (a, e, h, s, t) =>
        {
            agent = a; ev = e; host = h; sid = s; ts = t;
        };

        handler.Handle("1338;cmux-agent=claude;event=start;host=pnode16;pid=12345;sid=abc-def;ts=1700000000");

        agent.Should().Be("claude");
        ev.Should().Be("start");
        host.Should().Be("pnode16");
        sid.Should().Be("abc-def");
        ts.Should().NotBeNull();
        ts!.Value.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
    }

    [Fact]
    public void Osc1338_EndAnnounce_FiresWithEndEvent()
    {
        var handler = new OscHandler();
        string? ev = null;
        handler.AgentAnnounceReceived += (_, e, _, _, _) => ev = e;

        handler.Handle("1338;cmux-agent=claude;event=end;host=pnode16;pid=12345;sid=;ts=1700000005");

        ev.Should().Be("end");
    }

    [Fact]
    public void Osc1338_MissingRequiredKeys_DoesNotFire()
    {
        var handler = new OscHandler();
        bool fired = false;
        handler.AgentAnnounceReceived += (_, _, _, _, _) => fired = true;

        // No cmux-agent key.
        handler.Handle("1338;event=start;host=foo");
        // No event key.
        handler.Handle("1338;cmux-agent=claude;host=foo");

        fired.Should().BeFalse();
    }

    [Fact]
    public void Osc1338_EmptyOptionalFields_PassesNullsThrough()
    {
        var handler = new OscHandler();
        string? host = null;
        string? sid = null;
        bool fired = false;
        handler.AgentAnnounceReceived += (_, _, h, s, _) =>
        {
            fired = true;
            host = h;
            sid = s;
        };

        handler.Handle("1338;cmux-agent=claude;event=start;host=;sid=;ts=1700000000");

        fired.Should().BeTrue();
        host.Should().BeNull();
        sid.Should().BeNull();
    }

    [Fact]
    public void Osc7_WithHostAuthority_FiresBothCwdAndHost()
    {
        var handler = new OscHandler();
        string? cwd = null;
        string? host = null;
        handler.WorkingDirectoryChanged += d => cwd = d;
        handler.RemoteHostReported += h => host = h;

        handler.Handle("7;file://pnode16/work/foo");

        cwd.Should().Be("/work/foo");
        host.Should().Be("pnode16");
    }

    [Fact]
    public void Osc7_NoHostAuthority_OnlyFiresCwd()
    {
        var handler = new OscHandler();
        string? cwd = null;
        string? host = null;
        handler.WorkingDirectoryChanged += d => cwd = d;
        handler.RemoteHostReported += h => host = h;

        // file:///path (empty authority) — local cwd notification, no host.
        handler.Handle("7;file:///home/user");

        cwd.Should().Be("/home/user");
        host.Should().BeNull();
    }

    [Fact]
    public void Osc7_PlainPath_FallbackFiresCwd()
    {
        var handler = new OscHandler();
        string? cwd = null;
        handler.WorkingDirectoryChanged += d => cwd = d;

        handler.Handle("7;/var/log");

        cwd.Should().Be("/var/log");
    }
}

/// <summary>
/// AgentDetector.ClassifyPane decision-order regression coverage. The
/// announce signal (OSC 1338) must outrank process-tree heuristics so a
/// fresh cmuxw attaching to a daemon-cached pane gets the right answer
/// on the first frame.
/// </summary>
public class AgentDetectorAnnounceTests
{
    [Fact]
    public void Announce_ClaudeWithNoSshInTree_ClassifiesAsLocalClaude()
    {
        // pid=0 path: no WMI walk possible, decision comes entirely
        // from the announce. Mirrors the "daemon-cached announce
        // replayed before cmuxw learns the daemon's child PID" path.
        var kind = AgentDetector.ClassifyPane(
            shellPid: 0, bufferSnapshot: null, announcedAgent: "claude");
        kind.Should().Be(AgentDetector.PaneAgentKind.LocalClaude);
    }

    [Fact]
    public void Announce_Empty_PidZero_ReturnsUnknown()
    {
        var kind = AgentDetector.ClassifyPane(
            shellPid: 0, bufferSnapshot: null, announcedAgent: null);
        kind.Should().Be(AgentDetector.PaneAgentKind.Unknown);
    }

    [Fact]
    public void LooksLikeClaudeUI_KeyMarkers_ReturnTrue()
    {
        AgentDetector.LooksLikeClaudeUI("Welcome to Claude Code")
            .Should().BeTrue();
        AgentDetector.LooksLikeClaudeUI("hint: ? for shortcuts here")
            .Should().BeTrue();
        AgentDetector.LooksLikeClaudeUI("processing… (esc to interrupt)")
            .Should().BeTrue();
    }

    [Fact]
    public void LooksLikeClaudeUI_BoxDrawingAlone_ReturnsFalse()
    {
        // lazygit / fzf / htop use box drawing too — must NOT trigger
        // without the claude keyword co-occurring.
        AgentDetector.LooksLikeClaudeUI("╭ a header ╮\n│   body  │\n╰─────────╯")
            .Should().BeFalse();
    }

    [Fact]
    public void LooksLikeClaudeUI_KeywordAloneWithoutBox_ReturnsFalse()
    {
        AgentDetector.LooksLikeClaudeUI("the user ran claude once last week")
            .Should().BeFalse();
    }
}

/// <summary>
/// PaneStateSnapshot is the persistent contract between cmuxw runs. These
/// tests pin down round-tripping of the fields the daemon-kill /
/// cmuxw-restart restore flow depends on (cwd, remote cwd,
/// auto-restore command, claude session uuid, claude-running-inside
/// flag). A regression here translates directly into "my pane came
/// back but doesn't remember where it was".
/// </summary>
public class PaneStateSnapshotPersistenceTests
{
    [Fact]
    public void RoundTrip_PreservesAllRestoreCriticalFields()
    {
        var original = new PaneStateSnapshot
        {
            CapturedAt = new DateTime(2026, 5, 16, 9, 30, 0, DateTimeKind.Utc),
            WorkingDirectory = @"D:\work\ten1010",
            Shell = "powershell.exe",
            CommandHistory = new List<string> { "cd D:\\work", "ssh pnode16-root", "claude --continue" },
            AutoRestoreCommand = "ssh pnode16-root",
            RemoteWorkingDirectory = "/work/ysh",
            ClaudeRunningInside = true,
            ClaudeSessionUuid = "abcd-1234",
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var round = JsonSerializer.Deserialize<PaneStateSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        round.Should().NotBeNull();
        round!.WorkingDirectory.Should().Be(original.WorkingDirectory);
        round.Shell.Should().Be(original.Shell);
        round.CommandHistory.Should().BeEquivalentTo(original.CommandHistory);
        round.AutoRestoreCommand.Should().Be(original.AutoRestoreCommand);
        round.RemoteWorkingDirectory.Should().Be(original.RemoteWorkingDirectory);
        round.ClaudeRunningInside.Should().Be(original.ClaudeRunningInside);
        round.ClaudeSessionUuid.Should().Be(original.ClaudeSessionUuid);
    }

    [Fact]
    public void DefaultSnapshot_HasEmptyCollections_NotNull()
    {
        // Restore code dereferences CommandHistory without null check —
        // make sure a fresh / migrated snapshot has a usable list.
        var fresh = new PaneStateSnapshot();
        fresh.CommandHistory.Should().NotBeNull();
        fresh.CommandHistory.Should().BeEmpty();
    }
}

/// <summary>
/// End-to-end PTY-byte → TerminalSession integration. Feeds raw OSC
/// sequences through the same code path cmuxw / cmux-daemon use at
/// runtime (ConPTY → FeedOutput → VtParser → OscHandler) and asserts the
/// observable TerminalSession state matches. This is the closest we can
/// get to "live cmuxw verification" without spinning up the WPF
/// Application — it covers the entire signal pipeline end-to-end with
/// nothing mocked.
/// </summary>
public class TerminalSessionLiveIntegrationTests
{
    [Fact]
    public void Osc1338_StartAnnounce_PropagatesToSessionState()
    {
        var session = new TerminalSession("test-pane-1");
        var announces = new List<(string agent, string ev, string? host, string? sid)>();
        session.AgentAnnounceReceived += (a, e, h, s, _) =>
            announces.Add((a, e, h, s));

        // Exact wire format emitted by cmux-announce.sh on pnode16.
        var payload = "\x1b]1338;cmux-agent=claude;event=start;host=pnode16;pid=12345;sid=abc-def-ghi;ts=1700000000\x07";
        session.FeedOutput(Encoding.UTF8.GetBytes(payload));

        session.AnnouncedAgent.Should().Be("claude");
        session.RemoteHost.Should().Be("pnode16");
        session.AnnouncedAt.Should().NotBeNull();
        announces.Should().HaveCount(1);
        announces[0].agent.Should().Be("claude");
        announces[0].ev.Should().Be("start");
        announces[0].sid.Should().Be("abc-def-ghi");
    }

    [Fact]
    public void Osc1338_EndAnnounce_ClearsAnnouncedAgent()
    {
        var session = new TerminalSession("test-pane-2");

        session.FeedOutput(Encoding.UTF8.GetBytes(
            "\x1b]1338;cmux-agent=claude;event=start;host=pnode16;pid=1;sid=x;ts=1\x07"));
        session.AnnouncedAgent.Should().Be("claude");

        session.FeedOutput(Encoding.UTF8.GetBytes(
            "\x1b]1338;cmux-agent=claude;event=end;host=pnode16;pid=1;sid=x;ts=2\x07"));
        session.AnnouncedAgent.Should().BeNull();
        session.AnnouncedAt.Should().NotBeNull();
    }

    [Fact]
    public void Osc7_RemoteCwdAndHost_PopulateFromUnixUri()
    {
        var session = new TerminalSession("test-pane-3");
        string? cwd = null;
        string? host = null;
        session.WorkingDirectoryChanged += d => cwd = d;
        session.RemoteHostReported += h => host = h;

        // OSC 7 file://<host>/<path> — vte.sh on Ubuntu/Debian emits
        // this on every prompt. Bash PROMPT_COMMAND substitution.
        var payload = "\x1b]7;file://pnode16/var/log/nginx\x07";
        session.FeedOutput(Encoding.UTF8.GetBytes(payload));

        cwd.Should().Be("/var/log/nginx");
        host.Should().Be("pnode16");
        session.RemoteHost.Should().Be("pnode16");
        session.WorkingDirectory.Should().Be("/var/log/nginx");
    }

    [Fact]
    public void Osc_ChunkSplitAcrossMultipleFeeds_StillLexesCorrectly()
    {
        // ConPTY can deliver a single OSC sequence across several
        // chunked reads. VtParser must reassemble — otherwise long
        // OSC payloads (a 200-char body that crosses a 256-byte pipe
        // boundary) get silently dropped. This test pins that contract.
        var session = new TerminalSession("test-pane-4");
        session.AnnouncedAgent.Should().BeNull();

        var payload = "\x1b]1338;cmux-agent=claude;event=start;host=pnode16;pid=99;sid=xyz;ts=42\x07";
        var bytes = Encoding.UTF8.GetBytes(payload);

        // Split at byte 30 — middle of the payload, before terminator.
        session.FeedOutput(bytes.Take(30).ToArray());
        session.AnnouncedAgent.Should().BeNull(); // not yet — terminator not seen

        session.FeedOutput(bytes.Skip(30).ToArray());
        session.AnnouncedAgent.Should().Be("claude");
        session.RemoteHost.Should().Be("pnode16");
    }

    [Fact]
    public void Osc_MixedWithRegularOutput_OnlyOscAffectsAnnounceState()
    {
        var session = new TerminalSession("test-pane-5");
        var preamble = Encoding.UTF8.GetBytes("user@host:~$ claude\r\n");
        var osc = Encoding.UTF8.GetBytes(
            "\x1b]1338;cmux-agent=claude;event=start;host=h;pid=1;sid=s;ts=1\x07");
        var afterText = Encoding.UTF8.GetBytes("Welcome to Claude Code\r\n");

        session.FeedOutput(preamble);
        session.AnnouncedAgent.Should().BeNull();

        session.FeedOutput(osc);
        session.AnnouncedAgent.Should().Be("claude");

        session.FeedOutput(afterText);
        session.AnnouncedAgent.Should().Be("claude"); // unchanged by plain text
    }
}

/// <summary>
/// Verifies the full SessionPersistenceService round trip on a real
/// session.json file under a temp directory. Exercises the same Load →
/// in-memory mutation → Save → Load path cmuxw uses across restart
/// boundaries — including the daemon-kill / cmuxw-restart restore that
/// our /goal explicitly targets.
/// </summary>
public class SessionPersistenceLiveIntegrationTests
{
    [Fact]
    public void BuildState_JsonRoundTrip_PreservesPaneRestoreFields()
    {
        // SessionPersistenceService.StateDir / StatePath are `static
        // readonly` so we can't reflection-redirect them to a temp file
        // without clobbering the user's real session.json. Instead we
        // exercise the exact same code path: BuildState → JsonSerialize
        // (the serializer Save() uses internally) → JsonDeserialize.
        // This proves the schema round-trips losslessly across the
        // restart boundary that's load-bearing for our /goal —
        // anything broken here would surface as "my pane came back but
        // doesn't remember claude / cwd" after cmuxw restart.
        var workspace = new Workspace { Id = "ws1", Name = "Test WS" };
        var surface = new Surface { Id = "surf1", Name = "tab1", FocusedPaneId = "pane-ssh" };

        surface.PaneSnapshots["pane-ssh"] = new PaneStateSnapshot
        {
            WorkingDirectory = "/var/log",
            RemoteWorkingDirectory = "/var/log",
            AutoRestoreCommand = "ssh pnode16-root",
            ClaudeRunningInside = true,
            ClaudeSessionUuid = "claude-uuid-1234",
            CommandHistory = new List<string> { "ssh pnode16-root", "cd /var/log", "claude" },
        };
        surface.PaneSnapshots["pane-local"] = new PaneStateSnapshot
        {
            WorkingDirectory = @"D:\work\ten1010",
            CommandHistory = new List<string> { @"cd D:\work\ten1010", "git status" },
        };
        workspace.Surfaces.Add(surface);
        workspace.SelectedSurface = surface;

        var built = SessionPersistenceService.BuildState(
            new[] { workspace }, 0,
            100, 100, 1280, 800, false, 240, true, false);

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var json = JsonSerializer.Serialize(built, opts);
        var loaded = JsonSerializer.Deserialize<SessionState>(json, opts);

        loaded.Should().NotBeNull();
        loaded!.Workspaces.Should().HaveCount(1);

        var wsState = loaded.Workspaces[0];
        wsState.Surfaces.Should().HaveCount(1);
        var surfState = wsState.Surfaces[0];

        surfState.PaneSnapshots.Should().ContainKey("pane-ssh");
        var sshSnap = surfState.PaneSnapshots["pane-ssh"];
        sshSnap.WorkingDirectory.Should().Be("/var/log");
        sshSnap.RemoteWorkingDirectory.Should().Be("/var/log");
        sshSnap.AutoRestoreCommand.Should().Be("ssh pnode16-root");
        sshSnap.ClaudeRunningInside.Should().BeTrue();
        sshSnap.ClaudeSessionUuid.Should().Be("claude-uuid-1234");
        sshSnap.CommandHistory.Should().BeEquivalentTo(
            new[] { "ssh pnode16-root", "cd /var/log", "claude" });

        surfState.PaneSnapshots.Should().ContainKey("pane-local");
        var localSnap = surfState.PaneSnapshots["pane-local"];
        localSnap.WorkingDirectory.Should().Be(@"D:\work\ten1010");
        localSnap.CommandHistory.Should().BeEquivalentTo(
            new[] { @"cd D:\work\ten1010", "git status" });

        // Sanity: the split tree the user saw is preserved too. Pane
        // identity is what makes the per-pane restore meaningful — if
        // RootNode is dropped, panes pop back as un-named blanks.
        surfState.RootNode.Should().NotBeNull();
    }

    [Fact]
    public void AutoRestoreCommandTransformer_BareClaude_AppendsContinue()
    {
        // Saved primary = bare `claude`, no captured UUID. The Goal:
        // pane comes back with `claude --continue` (resume most-recent
        // chat in cwd), not a fresh empty session.
        var result = AutoRestoreCommandTransformer.Transform(
            "claude", capturedClaudeUuid: null, resumeClaude: true);
        result.Should().Be("claude --continue");
    }

    [Fact]
    public void AutoRestoreCommandTransformer_BareClaude_WithCapturedUuid_AppendsResume()
    {
        // We previously captured the session UUID via MaybeCaptureClaudeUuid.
        // Restore must hit *that exact session*, not most-recent (which is
        // ambiguous when multiple parallel claude sessions share a cwd).
        var uuid = "12345678-1234-1234-1234-123456789abc";
        var result = AutoRestoreCommandTransformer.Transform(
            "claude", capturedClaudeUuid: uuid, resumeClaude: true);
        result.Should().Be($"claude --resume {uuid}");
    }

    [Fact]
    public void AutoRestoreCommandTransformer_ClaudeContinueArg_WithCapturedUuid_PrefersUuid()
    {
        // User originally typed `claude --continue` but we managed to
        // capture the actual UUID afterwards. UUID is more deterministic
        // — strip --continue and append --resume <uuid>.
        var uuid = "12345678-1234-1234-1234-123456789abc";
        var result = AutoRestoreCommandTransformer.Transform(
            "claude --continue", capturedClaudeUuid: uuid, resumeClaude: true);
        result.Should().Be($"claude --resume {uuid}");
    }

    [Fact]
    public void AutoRestoreCommandTransformer_ClaudeWithDashC_WithCapturedUuid_PrefersUuid()
    {
        var uuid = "12345678-1234-1234-1234-123456789abc";
        var result = AutoRestoreCommandTransformer.Transform(
            "claude -c", capturedClaudeUuid: uuid, resumeClaude: true);
        result.Should().Be($"claude --resume {uuid}");
    }

    [Fact]
    public void AutoRestoreCommandTransformer_ExplicitResumeUuid_PreservedVerbatim()
    {
        // User pinned a specific session manually. Never clobber an
        // explicit pin — even if our captured UUID differs.
        var pinnedUuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var capturedUuid = "12345678-1234-1234-1234-123456789abc";
        var input = $"claude --resume {pinnedUuid}";
        var result = AutoRestoreCommandTransformer.Transform(
            input, capturedClaudeUuid: capturedUuid, resumeClaude: true);
        result.Should().Be(input);
    }

    [Fact]
    public void AutoRestoreCommandTransformer_ClaudeWithContinueNoUuid_PreservedVerbatim()
    {
        // No captured UUID + explicit --continue → keep as-is (claude
        // resumes most-recent in cwd, user's stated intent).
        var input = "claude --continue";
        var result = AutoRestoreCommandTransformer.Transform(
            input, capturedClaudeUuid: null, resumeClaude: true);
        result.Should().Be(input);
    }

    [Fact]
    public void AutoRestoreCommandTransformer_ClaudeWithPickerResumeNoUuid_PreservedVerbatim()
    {
        // Picker form (`claude --resume` with no UUID arg) → keep as-is
        // so claude shows the session list and the user chooses.
        var input = "claude --resume";
        var result = AutoRestoreCommandTransformer.Transform(
            input, capturedClaudeUuid: null, resumeClaude: true);
        result.Should().Be(input);
    }

    [Fact]
    public void AutoRestoreCommandTransformer_ResumeClaudeDisabled_PreservesVerbatim()
    {
        // ResumeClaudeOnRestore=false setting — no rewrites at all,
        // even when bare claude has a captured UUID.
        var uuid = "12345678-1234-1234-1234-123456789abc";
        var result = AutoRestoreCommandTransformer.Transform(
            "claude", capturedClaudeUuid: uuid, resumeClaude: false);
        result.Should().Be("claude");
    }

    [Fact]
    public void AutoRestoreCommandTransformer_SshCommand_AlwaysVerbatim()
    {
        // ssh / mosh commands aren't rewritten — soft-restore stage 2
        // (cd remote + claude --resume) runs separately after the ssh
        // handshake completes. Whatever ssh args the user originally
        // typed must replay verbatim.
        var inputs = new[]
        {
            "ssh pnode16-root",
            "ssh -p 2022 user@host",
            "ssh -J jumphost user@target",
            "mosh user@host",
        };
        foreach (var input in inputs)
        {
            var result = AutoRestoreCommandTransformer.Transform(
                input, capturedClaudeUuid: "any-uuid-here", resumeClaude: true);
            result.Should().Be(input, "ssh/mosh primary commands must never be rewritten");
        }
    }

    [Fact]
    public void AutoRestoreCommandTransformer_EmptyOrWhitespace_HandledGracefully()
    {
        AutoRestoreCommandTransformer.Transform("", null, true).Should().Be("");
        AutoRestoreCommandTransformer.Transform("   ", null, true).Should().Be("");
    }

    // ── CdCommandParser ────────────────────────────────────────────────
    // Mirrors the cd / D: tracking decision used inside SurfaceViewModel.
    // Covers user scenarios 1 (`cd D:\foo`), 2 (`D:` drive switch), 4
    // (SSH-side `cd`), and the corner cases that previously fell through
    // to prompt-parse alone (chains, quotes, ~ home).

    [Fact]
    public void CdParser_DriveSwitch_LocalToDriveRoot()
    {
        var r = CdCommandParser.Parse("D:", insideSsh: false,
            priorLocalCwd: @"C:\Users\u", priorRemoteCwd: null, fallbackLocalBase: null);
        r.LocalCwd.Should().Be(@"D:\");
        r.RemoteCwd.Should().BeNull();
    }

    [Fact]
    public void CdParser_DriveSwitch_LowercaseUppercased()
    {
        var r = CdCommandParser.Parse("d:", insideSsh: false,
            priorLocalCwd: null, priorRemoteCwd: null, fallbackLocalBase: null);
        r.LocalCwd.Should().Be(@"D:\");
    }

    [Fact]
    public void CdParser_DriveSwitch_IgnoredInsideSsh()
    {
        // Unix has no drives — refuse to fake-translate.
        var r = CdCommandParser.Parse("D:", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "/home/u", fallbackLocalBase: null);
        r.LocalCwd.Should().BeNull();
        r.RemoteCwd.Should().BeNull();
    }

    [Fact]
    public void CdParser_AbsoluteWindowsCd_SetsLocalCwd()
    {
        var r = CdCommandParser.Parse(@"cd D:\work\ten1010", insideSsh: false,
            priorLocalCwd: @"C:\Users\u", priorRemoteCwd: null, fallbackLocalBase: null);
        r.LocalCwd.Should().Be(@"D:\work\ten1010");
    }

    [Fact]
    public void CdParser_RelativeLocalCd_ResolvedAgainstPriorLocal()
    {
        var r = CdCommandParser.Parse("cd ten1010", insideSsh: false,
            priorLocalCwd: @"D:\work", priorRemoteCwd: null, fallbackLocalBase: null);
        r.LocalCwd.Should().Be(@"D:\work\ten1010");
    }

    [Fact]
    public void CdParser_RelativeLocalCd_FallsBackToBaseThenUserProfile()
    {
        // priorLocal = null, fallback supplied → fallback wins.
        var fallback = @"C:\fallback\base";
        var r = CdCommandParser.Parse("cd sub", insideSsh: false,
            priorLocalCwd: null, priorRemoteCwd: null, fallbackLocalBase: fallback);
        r.LocalCwd.Should().Be(@"C:\fallback\base\sub");
    }

    [Fact]
    public void CdParser_BareCdHomeRefused()
    {
        var r = CdCommandParser.Parse("cd", insideSsh: false,
            priorLocalCwd: @"D:\work", priorRemoteCwd: null, fallbackLocalBase: null);
        r.LocalCwd.Should().BeNull();
        r.RemoteCwd.Should().BeNull();
    }

    [Fact]
    public void CdParser_TildeHomeRefused()
    {
        var r = CdCommandParser.Parse("cd ~/foo", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "/home/u", fallbackLocalBase: null);
        r.RemoteCwd.Should().BeNull();
    }

    [Fact]
    public void CdParser_AndChainTrimmed_OnlyTargetUsed()
    {
        // `cd /work && claude` → target is /work, not "/work && claude".
        var r = CdCommandParser.Parse("cd /work && claude", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "/home/u", fallbackLocalBase: null);
        r.RemoteCwd.Should().Be("/work");
    }

    [Fact]
    public void CdParser_QuotedPathStripped()
    {
        var r = CdCommandParser.Parse(@"cd ""D:\path with spaces""", insideSsh: false,
            priorLocalCwd: null, priorRemoteCwd: null, fallbackLocalBase: @"C:\");
        r.LocalCwd.Should().Be(@"D:\path with spaces");
    }

    [Fact]
    public void CdParser_SshAbsoluteCd_ResetsRemoteCwd()
    {
        // Inside SSH: `cd /var/log` is absolute → wipes prior accumulation.
        var r = CdCommandParser.Parse("cd /var/log", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "ysh/foo", fallbackLocalBase: null);
        r.RemoteCwd.Should().Be("/var/log");
        r.LocalCwd.Should().BeNull();
    }

    [Fact]
    public void CdParser_SshRelativeCd_AccumulatesAgainstPriorRemote()
    {
        // Inside SSH: sequential `cd ysh`, `cd rax_shared` should combine.
        var r1 = CdCommandParser.Parse("cd ysh", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "", fallbackLocalBase: null);
        r1.RemoteCwd.Should().Be("ysh");

        var r2 = CdCommandParser.Parse("cd rax_shared", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "ysh", fallbackLocalBase: null);
        r2.RemoteCwd.Should().Be("ysh/rax_shared");

        var r3 = CdCommandParser.Parse("cd ..", insideSsh: true,
            priorLocalCwd: null, priorRemoteCwd: "ysh/rax_shared", fallbackLocalBase: null);
        r3.RemoteCwd.Should().Be("ysh");
    }

    [Fact]
    public void CdParser_NotACdCommand_NoChange()
    {
        var inputs = new[] { "ls -la", "git status", "claude", "echo hello", "" };
        foreach (var input in inputs)
        {
            var r = CdCommandParser.Parse(input, insideSsh: false,
                priorLocalCwd: @"C:\", priorRemoteCwd: null, fallbackLocalBase: null);
            r.LocalCwd.Should().BeNull(input);
            r.RemoteCwd.Should().BeNull(input);
        }
    }

    // ── PaneCommandTracker — SSH transition + AutoRestore capture ─────

    [Fact]
    public void SshTransition_SshEntersCorrectly()
    {
        PaneCommandTracker.ClassifySshTransition("ssh pnode16-root")
            .Should().Be(PaneCommandTracker.SshTransition.Enter);
        PaneCommandTracker.ClassifySshTransition("ssh -p 2022 user@host")
            .Should().Be(PaneCommandTracker.SshTransition.Enter);
        PaneCommandTracker.ClassifySshTransition("mosh user@host")
            .Should().Be(PaneCommandTracker.SshTransition.Enter);
        PaneCommandTracker.ClassifySshTransition("SSH host")
            .Should().Be(PaneCommandTracker.SshTransition.Enter);
    }

    [Fact]
    public void SshTransition_ExitOrLogoutLeaves()
    {
        PaneCommandTracker.ClassifySshTransition("exit")
            .Should().Be(PaneCommandTracker.SshTransition.Leave);
        PaneCommandTracker.ClassifySshTransition("logout")
            .Should().Be(PaneCommandTracker.SshTransition.Leave);
    }

    [Fact]
    public void SshTransition_NonSshNonExit_Neutral()
    {
        var inputs = new[] { "ls -la", "git status", "claude", "cd /tmp", "" };
        foreach (var input in inputs)
            PaneCommandTracker.ClassifySshTransition(input)
                .Should().Be(PaneCommandTracker.SshTransition.None, input);
    }

    [Fact]
    public void AutoRestoreCapture_FirstSshLikeCommand_CapturedAsPrimary()
    {
        var primaries = new[]
        {
            "ssh pnode16-root",
            "mosh user@host",
            "claude",
            "claude --resume abc",
            "tmux a",
            "screen -r",
        };
        foreach (var p in primaries)
        {
            PaneCommandTracker.ClassifyAutoRestoreCapture(p, existingPrimary: null)
                .Should().Be(PaneCommandTracker.CaptureKind.CaptureAsPrimary, p);
        }
    }

    [Fact]
    public void AutoRestoreCapture_FirstNonSession_NotCaptured()
    {
        var nonPrimary = new[] { "ls", "git status", "cd /tmp", "echo hi", "" };
        foreach (var n in nonPrimary)
            PaneCommandTracker.ClassifyAutoRestoreCapture(n, existingPrimary: null)
                .Should().Be(PaneCommandTracker.CaptureKind.None, n);
    }

    [Fact]
    public void AutoRestoreCapture_ClaudeAfterSshPrimary_MarkInsideSsh()
    {
        // This is the load-bearing branch for "Claude session 이어서":
        // Pane primary was an ssh, user now typed `claude` inside it
        // → flag the pane so soft-restore stage 2 runs cd remote + claude --resume.
        PaneCommandTracker.ClassifyAutoRestoreCapture(
                "claude", existingPrimary: "ssh pnode16-root")
            .Should().Be(PaneCommandTracker.CaptureKind.MarkClaudeInsideSsh);

        PaneCommandTracker.ClassifyAutoRestoreCapture(
                "claude --continue", existingPrimary: "mosh user@h")
            .Should().Be(PaneCommandTracker.CaptureKind.MarkClaudeInsideSsh);
    }

    [Fact]
    public void AutoRestoreCapture_NonClaudeAfterAnyPrimary_NoChange()
    {
        // Existing primary set; user types something unrelated. Don't
        // overwrite the primary; don't flag inside-ssh.
        var primaries = new[] { "ssh host", "claude", "tmux", "screen" };
        var followups = new[] { "ls", "cd /tmp", "git pull", "vim file" };
        foreach (var p in primaries)
            foreach (var f in followups)
                PaneCommandTracker.ClassifyAutoRestoreCapture(f, existingPrimary: p)
                    .Should().Be(PaneCommandTracker.CaptureKind.None,
                        $"primary='{p}', followup='{f}'");
    }

    [Fact]
    public void AutoRestoreCapture_ClaudeAfterLocalClaudePrimary_NoChange()
    {
        // Primary already claude (no ssh wrapping) → typing claude
        // again inside doesn't flag inside-ssh. The ssh+claude flag is
        // only meaningful when stage-2 cd-remote replay actually
        // happens.
        PaneCommandTracker.ClassifyAutoRestoreCapture(
                "claude", existingPrimary: "claude")
            .Should().Be(PaneCommandTracker.CaptureKind.None);
    }

    [Fact]
    public void AutoRestoreCommandTransformer_HasExplicitResumeUuid_DistinguishesPickerFromPinned()
    {
        AutoRestoreCommandTransformer.HasExplicitResumeUuid("claude --resume").Should().BeFalse();
        AutoRestoreCommandTransformer.HasExplicitResumeUuid("claude --resume notuuid")
            .Should().BeFalse();
        AutoRestoreCommandTransformer.HasExplicitResumeUuid(
            "claude --resume 12345678-1234-1234-1234-123456789abc").Should().BeTrue();
        AutoRestoreCommandTransformer.HasExplicitResumeUuid(
            "claude -r 12345678-1234-1234-1234-123456789abc").Should().BeTrue();
        AutoRestoreCommandTransformer.HasExplicitResumeUuid("claude -r").Should().BeFalse();
    }

    [Fact]
    public void BuildState_RealFileSave_RoundTripsViaUserPath_Reversibly()
    {
        // End-to-end through the real File.WriteAllText / ReadAllText
        // path the service uses. We back up & restore any pre-existing
        // session.json so the user's running cmuxw isn't disturbed.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var realPath = Path.Combine(localAppData, "cmux", "session.json");
        var backupPath = realPath + ".test-backup-" + Guid.NewGuid().ToString("N");
        var hadOriginal = File.Exists(realPath);

        try
        {
            if (hadOriginal)
                File.Move(realPath, backupPath);

            var workspace = new Workspace
            {
                Id = "live-int-ws",
                Name = "Live Integration",
            };
            var surface = new Surface
            {
                Id = "live-int-surf",
                Name = "tab",
                FocusedPaneId = "pid-1",
            };
            surface.PaneSnapshots["pid-1"] = new PaneStateSnapshot
            {
                WorkingDirectory = "/work/ysh",
                RemoteWorkingDirectory = "/work/ysh",
                AutoRestoreCommand = "ssh pnode16-root",
                ClaudeRunningInside = true,
                ClaudeSessionUuid = "deadbeef-cafe",
                CommandHistory = new List<string> { "ssh pnode16-root", "cd /work/ysh", "claude" },
            };
            workspace.Surfaces.Add(surface);
            workspace.SelectedSurface = surface;

            var built = SessionPersistenceService.BuildState(
                new[] { workspace }, 0,
                0, 0, 1000, 800, false, 240, true, false);
            SessionPersistenceService.Save(built);

            File.Exists(realPath).Should().BeTrue();

            var loaded = SessionPersistenceService.Load();
            loaded.Should().NotBeNull();
            loaded!.Workspaces.Should().ContainSingle(w => w.Id == "live-int-ws");
            var snap = loaded.Workspaces[0].Surfaces[0].PaneSnapshots["pid-1"];
            snap.AutoRestoreCommand.Should().Be("ssh pnode16-root");
            snap.ClaudeRunningInside.Should().BeTrue();
            snap.ClaudeSessionUuid.Should().Be("deadbeef-cafe");
        }
        finally
        {
            // Always restore. If backup exists, that wins; else delete
            // the test-written file so the user isn't left with a
            // half-fake session state.
            try
            {
                if (File.Exists(realPath))
                    File.Delete(realPath);
                if (File.Exists(backupPath))
                    File.Move(backupPath, realPath);
            }
            catch { /* test cleanup is best-effort */ }
        }
    }
}

