using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Modding;

namespace DevTools;

/// <summary>
/// Development helper: a localhost HTTP endpoint for setting up, inspecting and screenshotting
/// game screens, so mods can be checked without the game window being in front.
///
///   GET  /screenshot?path=/abs/file.png   save the current frame as PNG
///   POST /mouse/move?x=..&amp;y=..            move the in-game mouse
///   POST /mouse/click?x=..&amp;y=..           left click at that position
///   GET  /window                          window size in pixels and mode
///   POST /window/mode?mode=windowed       a window beside other apps, for watching while tools
///                                         drive the game. mode=saved: back to the player's
///                                         display setting. Neither is saved.
///   POST /console                         run the request body as a dev console command
///   GET  /inspect                         cards, relics, potions, badges and hover tips on screen
///   GET  /tree?type=NSettingsScreen&amp;depth=4  node subtree with types, rects and texts
///   POST /key?name=Escape                 press and release a key (Godot Key name)
///   POST /call?type=NMerchantRoom&amp;method=OpenInventory   call a method on the first node of a type
///
/// Mouse coordinates are window pixels, which are also the pixels of a screenshot, so a
/// position can be read straight off a screenshot. POST requests need a body (curl -d '').
/// </summary>
[ModInitializer("Initialize")]
public static class DevToolsMod
{
    private const string Prefix = "http://localhost:15527/";

    private static readonly ConcurrentQueue<Action> MainThreadQueue = new();

    private static DevConsole? _console;

    public static void Initialize()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += () =>
        {
            while (MainThreadQueue.TryDequeue(out var action))
                action();
        };

        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        new Thread(() => Serve(listener)) { IsBackground = true, Name = "DevTools HTTP" }.Start();
        GD.Print($"[DevTools] Listening on {Prefix}");
    }

    // An exception escaping this thread would abort the whole game process, so nothing may.
    private static void Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = listener.GetContext();
                string body;
                int status;
                try
                {
                    body = Handle(ctx.Request);
                    status = 200;
                }
                catch (Exception ex)
                {
                    body = $"{{\"error\": {Json(ex.Message)}}}";
                    status = 500;
                }
                Respond(ctx.Response, status, body);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[DevTools] Request failed: {ex.Message}");
            }
        }
    }

    private static string Handle(HttpListenerRequest req)
    {
        var path = req.Url?.AbsolutePath ?? "/";
        var q = req.QueryString;
        switch (path)
        {
            case "/screenshot":
            {
                var file = q["path"] ?? throw new ArgumentException("missing path");
                RunOnMainThread(() =>
                {
                    // The game stops drawing while its window is hidden, so render a fresh frame.
                    RenderingServer.ForceDraw(swapBuffers: false);
                    var image = ((SceneTree)Engine.GetMainLoop()).Root.GetTexture().GetImage();
                    var err = image.SavePng(file);
                    if (err != Error.Ok)
                        throw new IOException($"SavePng failed: {err}");
                    return 0;
                });
                return $"{{\"path\": {Json(file)}}}";
            }
            case "/console":
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var command = reader.ReadToEnd().Trim();
                if (command.Length == 0)
                    throw new ArgumentException("missing command in request body");
                var result = RunOnMainThread(() =>
                {
                    _console ??= new DevConsole(shouldAllowDebugCommands: true);
                    return _console.ProcessCommand(command);
                });
                var finished = result.task == null || result.task.Wait(TimeSpan.FromSeconds(30));
                return $"{{\"success\": {(result.success ? "true" : "false")}, \"finished\": {(finished ? "true" : "false")}, \"message\": {Json(result.msg ?? "")}}}";
            }
            case "/call":
            {
                var typeName = q["type"] ?? throw new ArgumentException("missing type");
                var method = q["method"] ?? throw new ArgumentException("missing method");
                var result = RunOnMainThread(() =>
                {
                    var node = Inspector.FindByType(typeName) ?? throw new ArgumentException($"no {typeName} in tree");
                    return node.Call(method).ToString();
                });
                return $"{{\"result\": {Json(result)}}}";
            }
            case "/inspect":
                return System.Text.Json.JsonSerializer.Serialize(RunOnMainThread(Inspector.Inspect));
            case "/tree":
            {
                var typeName = q["type"] ?? throw new ArgumentException("missing type");
                var depth = int.Parse(q["depth"] ?? "3", CultureInfo.InvariantCulture);
                return System.Text.Json.JsonSerializer.Serialize(RunOnMainThread(() => Inspector.Tree(typeName, depth)));
            }
            case "/key":
            {
                var key = Enum.Parse<Key>(q["name"] ?? throw new ArgumentException("missing name"));
                RunOnMainThread(() =>
                {
                    Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
                    return 0;
                });
                return "{\"status\": \"ok\"}";
            }
            case "/window":
            {
                var (size, mode) = RunOnMainThread(() => (DisplayServer.WindowGetSize(), DisplayServer.WindowGetMode()));
                return $"{{\"width\": {size.X}, \"height\": {size.Y}, \"mode\": \"{mode}\"}}";
            }
            case "/window/mode":
            {
                var mode = q["mode"];
                if (mode is not ("windowed" or "saved"))
                    throw new ArgumentException("mode must be windowed or saved");
                RunOnMainThread(() =>
                {
                    if (mode == "windowed")
                        Watchable();
                    else
                        Saved();
                    return 0;
                });
                return "{\"status\": \"ok\"}";
            }
            case "/mouse/move":
            case "/mouse/click":
            {
                var pos = new Vector2(ParseFloat(q["x"]), ParseFloat(q["y"]));
                var click = path == "/mouse/click";
                RunOnMainThread(() =>
                {
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                    if (click)
                    {
                        Input.ParseInputEvent(new InputEventMouseButton
                            { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true });
                        Input.ParseInputEvent(new InputEventMouseButton
                            { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false });
                    }
                    return 0;
                });
                return "{\"status\": \"ok\"}";
            }
            default:
                throw new ArgumentException($"unknown endpoint {path}");
        }
    }

    // A 16:9 window of 60% of the screen's width (at least 1280 pixels), at the screen's top
    // right, so the game can be watched beside other apps. The game's own display setting stays
    // as it is. The game saves the window's size on quit only when that setting is windowed.
    private static void Watchable()
    {
        var screen = DisplayServer.WindowGetCurrentScreen();
        var area = DisplayServer.ScreenGetUsableRect(screen);
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        var width = Math.Min(area.Size.X, Math.Max(1280, area.Size.X * 6 / 10));
        var size = new Vector2I(width, width * 9 / 16);
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(new Vector2I(area.End.X - size.X, area.Position.Y));
    }

    // Back to the player's setting, as the game applies it at start.
    private static void Saved()
    {
        var settings = MegaCrit.Sts2.Core.Saves.SaveManager.Instance.SettingsSave;
        if (settings.Fullscreen)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            return;
        }
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetSize(settings.WindowSize);
        DisplayServer.WindowSetPosition(DisplayServer.ScreenGetPosition(settings.TargetDisplay) + settings.WindowPosition);
    }

    // Godot objects may only be touched on the main thread, so wait there for the result.
    private static T RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThreadQueue.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("main thread did not respond");
        return tcs.Task.Result;
    }

    private static float ParseFloat(string? s) =>
        float.Parse(s ?? throw new ArgumentException("missing coordinate"), CultureInfo.InvariantCulture);

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static void Respond(HttpListenerResponse resp, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        resp.StatusCode = status;
        resp.ContentType = "application/json";
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }
}
