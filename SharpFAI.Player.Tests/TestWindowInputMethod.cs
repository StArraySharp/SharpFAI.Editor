using System.Runtime.InteropServices;
using NUnit.Framework;

namespace SharpFAI.Player.Tests;

[TestFixture]
[Platform("Win")]
public class TestWindowInputMethod
{
    private const int Forwarded = 42;

    [Test]
    public void SelectionOrPlayBlocksLanguageRequestsUntilTextFocusOrExit()
    {
        nint window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        Assert.That(window, Is.Not.Zero);
        SubclassProc probe = HandleLanguageRequest;
        try
        {
            Assert.That(SetWindowSubclass(window, probe, 0, 0), Is.True);
            nint originalContext = ReadContext(window);
            using WindowInputMethod input = new(window);
            Check(Forwarded, originalContext);

            input.Update(focused: true, selectingOrPlaying: true, textFocused: false);
            input.Update(focused: true, selectingOrPlaying: true, textFocused: false);
            Check(0, 0);

            input.Update(focused: true, selectingOrPlaying: true, textFocused: true);
            Check(Forwarded, originalContext);
            input.Update(focused: true, selectingOrPlaying: true, textFocused: false);
            Check(0, 0);

            input.Update(focused: true, selectingOrPlaying: false, textFocused: false);
            Check(Forwarded, originalContext);
            input.Update(focused: true, selectingOrPlaying: true, textFocused: false);
            input.Restore(); // Window focus was lost.
            Check(Forwarded, originalContext);
            input.Update(focused: false, selectingOrPlaying: true, textFocused: false);
            Check(Forwarded, originalContext);

            input.Update(focused: true, selectingOrPlaying: true, textFocused: false);
            Check(0, 0);
            input.Dispose();
            Check(Forwarded, originalContext);
        }
        finally
        {
            DestroyWindow(window);
            GC.KeepAlive(probe);
        }

        void Check(int expectedResult, nint expectedContext)
        {
            // The probe consumes even forwarded requests, so the test never changes the input language.
            Assert.That(SendMessage(window, WindowInputMethod.InputLanguageChangeRequest, 0, 0),
                Is.EqualTo((nint)expectedResult));
            Assert.That(ReadContext(window), Is.EqualTo(expectedContext));
        }
    }

    private static nint ReadContext(nint window)
    {
        nint context = ImmGetContext(window);
        if (context != 0)
            ImmReleaseContext(window, context);
        return context;
    }

    private static nint HandleLanguageRequest(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data) =>
        message == WindowInputMethod.InputLanguageChangeRequest ? Forwarded : DefSubclassProc(window, message, wParam, lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern nint ImmGetContext(nint window);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern bool ImmReleaseContext(nint window, nint context);
}
