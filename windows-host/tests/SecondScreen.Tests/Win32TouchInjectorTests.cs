using System.Drawing;
using System.Runtime.InteropServices;
using SecondScreen.Core.Protocol;
using SecondScreen.Input;
using Xunit;

namespace SecondScreen.Tests;

public class Win32TouchInjectorTests
{
    [Fact]
    public void Win32TouchInjector_InitializeAndInject_ExecutesWithoutError()
    {
        using var injector = new Win32TouchInjector();
        bool init = injector.Initialize(10, TouchFeedbackMode.None);
        // On Windows 10/11, InitializeTouchInjection should succeed (or error 87 if already initialized by process)
        Assert.True(init, "InitializeTouchInjection failed");

        // Down
        var pDown = new[]
        {
            new PointerEntry(PointerId: 0, Action: TouchAction.Down, NormalizedX: 10000, NormalizedY: 10000, ContactRadius: 10)
        };
        bool downOk = injector.InjectTouch(new TouchEventMessage(1, 0, pDown));
        int errDown = Marshal.GetLastWin32Error();

        // Up
        var pUp = new[]
        {
            new PointerEntry(PointerId: 0, Action: TouchAction.Up, NormalizedX: 10000, NormalizedY: 10000, ContactRadius: 10)
        };
        bool upOk = injector.InjectTouch(new TouchEventMessage(1, 0, pUp));
        int errUp = Marshal.GetLastWin32Error();

        Assert.True(downOk, $"InjectTouch DOWN failed with Win32 error: {errDown}");
        Assert.True(upOk, $"InjectTouch UP failed with Win32 error: {errUp}");

        // LeftClick
        bool clickOk = injector.InjectTouch(new TouchEventMessage(1, 0, new[] {
            new PointerEntry(PointerId: 0, Action: TouchAction.LeftClick, NormalizedX: 20000, NormalizedY: 20000)
        }));
        Assert.True(clickOk);

        // RightClick
        bool rClickOk = injector.InjectTouch(new TouchEventMessage(1, 0, new[] {
            new PointerEntry(PointerId: 0, Action: TouchAction.RightClick, NormalizedX: 20000, NormalizedY: 20000)
        }));
        Assert.True(rClickOk);

        // Scroll
        bool scrollOk = injector.InjectTouch(new TouchEventMessage(1, 0, new[] {
            new PointerEntry(PointerId: 0, Action: TouchAction.Scroll, NormalizedX: 20000, NormalizedY: 120)
        }));
        Assert.True(scrollOk);
    }
}

