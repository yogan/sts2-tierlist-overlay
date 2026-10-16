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
///   GET  /window                          window size in pixels
///   POST /console                         run the request body as a dev console command
///   GET  /inspect                         cards, relics, potions, badges and hover tips on screen
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
                    // The game stops drawing while its window is hidden; render a fresh frame.
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
            case "/window":
            {
                var size = RunOnMainThread(() => DisplayServer.WindowGetSize());
                return $"{{\"width\": {size.X}, \"height\": {size.Y}}}";
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

    // Godot objects may only be touched on the main thread; wait there for the result.
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
