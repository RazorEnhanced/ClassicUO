﻿#region license

// Copyright (c) 2024, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SDL2;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using TinyEcs;

namespace ClassicUO
{
    internal static class Bootstrap
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);


        [UnmanagedCallersOnly(EntryPoint = "Initialize", CallConvs = new Type[] { typeof(CallConvCdecl) })]
        static unsafe void Initialize(IntPtr* argv, int argc, HostBindings* hostSetup)
        {
            var args = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                args[i] = Marshal.PtrToStringAnsi(argv[i]);
            }

            var host = new UnmanagedAssistantHost(hostSetup);
            Boot(host, args);
        }


        [STAThread]
        public static void Main(string[] args) => Boot(null, args);


        public static void Boot(UnmanagedAssistantHost pluginHost, string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Log.Start(LogTypes.All);

            CUOEnviroment.GameThread = Thread.CurrentThread;
            CUOEnviroment.GameThread.Name = "CUO_MAIN_THREAD";
#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("######################## [START LOG] ########################");

#if DEV_BUILD
                sb.AppendLine($"ClassicUO [DEV_BUILD] - {CUOEnviroment.Version} - {DateTime.Now}");
#else
                sb.AppendLine($"ClassicUO [STANDARD_BUILD] - {CUOEnviroment.Version} - {DateTime.Now}");
#endif

                sb.AppendLine
                    ($"OS: {Environment.OSVersion.Platform} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");

                sb.AppendLine($"Thread: {Thread.CurrentThread.Name}");
                sb.AppendLine();

                if (Settings.GlobalSettings != null)
                {
                    sb.AppendLine($"Shard: {Settings.GlobalSettings.IP}");
                    sb.AppendLine($"ClientVersion: {Settings.GlobalSettings.ClientVersion}");
                    sb.AppendLine();
                }

                sb.AppendFormat("Exception:\n{0}\n", e.ExceptionObject);
                sb.AppendLine("######################## [END LOG] ########################");
                sb.AppendLine();
                sb.AppendLine();

                Log.Panic(e.ExceptionObject.ToString());
                string path = Path.Combine(CUOEnviroment.ExecutablePath, "Logs");

                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);

                using (LogFile crashfile = new LogFile(path, "crash.txt"))
                {
                    crashfile.WriteAsync(sb.ToString()).RunSynchronously();
                }
            };
#endif
            ReadSettingsFromArgs(args);

            if (CUOEnviroment.IsHighDPI)
            {
                Environment.SetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI", "1");
            }

            //Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", "OpenGL");

            // NOTE: this is a workaroud to fix d3d11 on windows 11 + scale windows
            Environment.SetEnvironmentVariable("FNA3D_D3D11_FORCE_BITBLT", "1");

            Environment.SetEnvironmentVariable("FNA3D_BACKBUFFER_SCALE_NEAREST", "1");
            Environment.SetEnvironmentVariable("FNA3D_OPENGL_FORCE_COMPATIBILITY_PROFILE", "1");
            Environment.SetEnvironmentVariable(SDL.SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");

            Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH") + ";" + Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Plugins"));

            string globalSettingsPath = Settings.GetSettingsFilepath();

            if (!Directory.Exists(Path.GetDirectoryName(globalSettingsPath)) || !File.Exists(globalSettingsPath))
            {
                // settings specified in path does not exists, make new one
                {
                    // TODO:
                    Settings.GlobalSettings.Save();
                }
            }

            Settings.GlobalSettings = ConfigurationResolver.Load<Settings>(globalSettingsPath, SettingsJsonContext.RealDefault.Settings);
            CUOEnviroment.IsOutlands = Settings.GlobalSettings.ShardType == 2;

            ReadSettingsFromArgs(args);

            // still invalid, cannot load settings
            if (Settings.GlobalSettings == null)
            {
                Settings.GlobalSettings = new Settings();
                Settings.GlobalSettings.Save();
            }

            if (!CUOEnviroment.IsUnix)
            {
                string libsPath = Path.Combine(CUOEnviroment.ExecutablePath, Environment.Is64BitProcess ? "x64" : "x86");

                SetDllDirectory(libsPath);
            }

            if (string.IsNullOrWhiteSpace(Settings.GlobalSettings.Language))
            {
                Log.Trace("language is not set. Trying to get the OS language.");
                try
                {
                    Settings.GlobalSettings.Language = CultureInfo.InstalledUICulture.ThreeLetterWindowsLanguageName;

                    if (string.IsNullOrWhiteSpace(Settings.GlobalSettings.Language))
                    {
                        Log.Warn("cannot read the OS language. Rolled back to ENU");

                        Settings.GlobalSettings.Language = "ENU";
                    }

                    Log.Trace($"language set: '{Settings.GlobalSettings.Language}'");
                }
                catch
                {
                    Log.Warn("cannot read the OS language. Rolled back to ENU");

                    Settings.GlobalSettings.Language = "ENU";
                }
            }

            if (string.IsNullOrWhiteSpace(Settings.GlobalSettings.UltimaOnlineDirectory))
            {
                Settings.GlobalSettings.UltimaOnlineDirectory = CUOEnviroment.ExecutablePath;
            }

            const uint INVALID_UO_DIRECTORY = 0x100;
            const uint INVALID_UO_VERSION = 0x200;

            uint flags = 0;

            if (!Directory.Exists(Settings.GlobalSettings.UltimaOnlineDirectory) || !File.Exists(Path.Combine(Settings.GlobalSettings.UltimaOnlineDirectory, "tiledata.mul")))
            {
                flags |= INVALID_UO_DIRECTORY;
            }

            string clientVersionText = Settings.GlobalSettings.ClientVersion;

            if (!ClientVersionHelper.IsClientVersionValid(Settings.GlobalSettings.ClientVersion, out ClientVersion clientVersion))
            {
                Log.Warn($"Client version [{clientVersionText}] is invalid, let's try to read the client.exe");

                // mmm something bad happened, try to load from client.exe [windows only]
                if (!ClientVersionHelper.TryParseFromFile(Path.Combine(Settings.GlobalSettings.UltimaOnlineDirectory, "client.exe"), out clientVersionText) || !ClientVersionHelper.IsClientVersionValid(clientVersionText, out clientVersion))
                {
                    Log.Error("Invalid client version: " + clientVersionText);

                    flags |= INVALID_UO_VERSION;
                }
                else
                {
                    Log.Trace($"Found a valid client.exe [{clientVersionText} - {clientVersion}]");

                    // update the wrong/missing client version in settings.json
                    Settings.GlobalSettings.ClientVersion = clientVersionText;
                }
            }

            if (flags != 0)
            {
                if ((flags & INVALID_UO_DIRECTORY) != 0)
                {
                    Client.ShowErrorMessage(ResGeneral.YourUODirectoryIsInvalid);
                }
                else if ((flags & INVALID_UO_VERSION) != 0)
                {
                    Client.ShowErrorMessage(ResGeneral.YourUOClientVersionIsInvalid);
                }

                PlatformHelper.LaunchBrowser(ResGeneral.ClassicUOLink);
            }
            else
            {
                switch (Settings.GlobalSettings.ForceDriver)
                {
                    case 1: // OpenGL
                        Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", "OpenGL");

                        break;

                    case 2: // Vulkan
                        Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", "Vulkan");

                        break;
                }

                var ecs = new TinyEcs.World();
                var scheduler = new Scheduler(ecs);

                scheduler.AddPlugin<MainPlugin>();
                while (true)
                    scheduler.Run();

                //Client.Run(pluginHost);
            }

            Log.Trace("Closing...");
        }

        private static void ReadSettingsFromArgs(string[] args)
        {
            for (int i = 0; i <= args.Length - 1; i++)
            {
                string cmd = args[i].ToLower();

                // NOTE: Command-line option name should start with "-" character
                if (cmd.Length == 0 || cmd[0] != '-')
                {
                    continue;
                }

                cmd = cmd.Remove(0, 1);
                string value = string.Empty;

                if (i < args.Length - 1)
                {
                    if (!string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("-"))
                    {
                        value = args[++i];
                    }
                }

                Log.Trace($"ARG: {cmd}, VALUE: {value}");

                switch (cmd)
                {
                    // Here we have it! Using `-settings` option we can now set the filepath that will be used
                    // to load and save ClassicUO main settings instead of default `./settings.json`
                    // NOTE: All individual settings like `username`, `password`, etc passed in command-line options
                    // will override and overwrite those in the settings file because they have higher priority
                    case "settings":
                        Settings.CustomSettingsFilepath = value;

                        break;

                    case "highdpi":
                        CUOEnviroment.IsHighDPI = true;

                        break;

                    case "username":
                        Settings.GlobalSettings.Username = value;

                        break;

                    case "password":
                        Settings.GlobalSettings.Password = Crypter.Encrypt(value);

                        break;

                    case "password_enc": // Non-standard setting, similar to `password` but for already encrypted password
                        Settings.GlobalSettings.Password = value;

                        break;

                    case "ip":
                        Settings.GlobalSettings.IP = value;

                        break;

                    case "port":
                        Settings.GlobalSettings.Port = ushort.Parse(value);

                        break;

                    case "filesoverride":
                    case "uofilesoverride":
                        UOFilesOverrideMap.OverrideFile = value;

                        break;

                    case "ultimaonlinedirectory":
                    case "uopath":
                        Settings.GlobalSettings.UltimaOnlineDirectory = value;

                        break;

                    case "profilespath":
                        Settings.GlobalSettings.ProfilesPath = value;

                        break;

                    case "clientversion":
                        Settings.GlobalSettings.ClientVersion = value;

                        break;

                    case "lastcharactername":
                    case "lastcharname":
                        LastCharacterManager.OverrideLastCharacter(value);

                        break;

                    case "lastservernum":
                        Settings.GlobalSettings.LastServerNum = ushort.Parse(value);

                        break;

                    case "last_server_name":
                        Settings.GlobalSettings.LastServerName = value;
                        break;

                    case "fps":
                        int v = int.Parse(value);

                        if (v < Constants.MIN_FPS)
                        {
                            v = Constants.MIN_FPS;
                        }
                        else if (v > Constants.MAX_FPS)
                        {
                            v = Constants.MAX_FPS;
                        }

                        Settings.GlobalSettings.FPS = v;

                        break;

                    case "debug":
                        CUOEnviroment.Debug = true;

                        break;

                    case "profiler":
                        Profiler.Enabled = bool.Parse(value);

                        break;

                    case "saveaccount":
                        Settings.GlobalSettings.SaveAccount = bool.Parse(value);

                        break;

                    case "autologin":
                        Settings.GlobalSettings.AutoLogin = bool.Parse(value);

                        break;

                    case "reconnect":
                        Settings.GlobalSettings.Reconnect = bool.Parse(value);

                        break;

                    case "reconnect_time":

                        if (!int.TryParse(value, out int reconnectTime) || reconnectTime < 1000)
                        {
                            reconnectTime = 1000;
                        }

                        Settings.GlobalSettings.ReconnectTime = reconnectTime;

                        break;

                    case "login_music":
                    case "music":
                        Settings.GlobalSettings.LoginMusic = bool.Parse(value);

                        break;

                    case "login_music_volume":
                    case "music_volume":
                        Settings.GlobalSettings.LoginMusicVolume = int.Parse(value);

                        break;

                    // ======= [SHARD_TYPE_FIX] =======
                    // TODO old. maintain it for retrocompatibility
                    case "shard_type":
                    case "shard":
                        Settings.GlobalSettings.ShardType = int.Parse(value);

                        break;
                    // ================================

                    case "outlands":
                        CUOEnviroment.IsOutlands = true;

                        break;

                    case "fixed_time_step":
                        Settings.GlobalSettings.FixedTimeStep = bool.Parse(value);

                        break;

                    case "skiploginscreen":
                        CUOEnviroment.SkipLoginScreen = true;

                        break;

                    case "plugins":
                        Settings.GlobalSettings.Plugins = string.IsNullOrEmpty(value) ? new string[0] : value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                        break;

                    case "use_verdata":
                        Settings.GlobalSettings.UseVerdata = bool.Parse(value);

                        break;

                    case "maps_layouts":

                        Settings.GlobalSettings.MapsLayouts = value;

                        break;

                    case "encryption":
                        Settings.GlobalSettings.Encryption = byte.Parse(value);

                        break;

                    case "force_driver":
                        if (byte.TryParse(value, out byte res))
                        {
                            switch (res)
                            {
                                case 1: // OpenGL
                                    Settings.GlobalSettings.ForceDriver = 1;

                                    break;

                                case 2: // Vulkan
                                    Settings.GlobalSettings.ForceDriver = 2;

                                    break;

                                default: // use default
                                    Settings.GlobalSettings.ForceDriver = 0;

                                    break;
                            }
                        }
                        else
                        {
                            Settings.GlobalSettings.ForceDriver = 0;
                        }

                        break;

                    case "packetlog":

                        PacketLogger.Default.Enabled = true;
                        PacketLogger.Default.CreateFile();

                        break;

                    case "language":

                        switch (value?.ToUpperInvariant())
                        {
                            case "RUS": Settings.GlobalSettings.Language = "RUS"; break;
                            case "FRA": Settings.GlobalSettings.Language = "FRA"; break;
                            case "DEU": Settings.GlobalSettings.Language = "DEU"; break;
                            case "ESP": Settings.GlobalSettings.Language = "ESP"; break;
                            case "JPN": Settings.GlobalSettings.Language = "JPN"; break;
                            case "KOR": Settings.GlobalSettings.Language = "KOR"; break;
                            case "PTB": Settings.GlobalSettings.Language = "PTB"; break;
                            case "ITA": Settings.GlobalSettings.Language = "ITA"; break;
                            case "CHT": Settings.GlobalSettings.Language = "CHT"; break;
                            default:

                                Settings.GlobalSettings.Language = "ENU";
                                break;

                        }

                        break;

                    case "no_server_ping":

                        CUOEnviroment.NoServerPing = true;

                        break;
                }
            }
        }
    }

    struct Renderable
    {
        public Texture2D Texture;
        public Vector2 Position;
        public Vector3 Color;
        public Rectangle UV;
    }

    readonly struct MainPlugin : IPlugin
    {
        public void Build(Scheduler scheduler)
        {
            scheduler.AddPlugin(new FnaPlugin() {
                WindowResizable = true,
                MouseVisible = true,
                VSync = true, // don't kill the gpu
            });

            scheduler.AddPlugin<CuoPlugin>();
        }
    }


    // TODO: just for test
    readonly struct CuoPlugin : IPlugin
    {
        public unsafe void Build(Scheduler scheduler)
        {
            scheduler.AddSystem(static (Res<GraphicsDevice> device, SchedulerState schedState) => {
                ClientVersionHelper.IsClientVersionValid(Settings.GlobalSettings.ClientVersion, out ClientVersion clientVersion);
                Assets.UOFileManager.Load(clientVersion, Settings.GlobalSettings.UltimaOnlineDirectory, false, "ENU");

                schedState.AddResource(new Renderer.Arts.Art(device));
                schedState.AddResource(Assets.MapLoader.Instance);
                schedState.AddResource(new Renderer.UltimaBatcher2D(device));

            }, Stages.Startup);

            // scheduler.AddSystem(static (TinyEcs.World world, Res<Renderer.Arts.Art> arts) => {
            //     var maxX = 500;
            //     var maxY = 0;
            //     var x = 0;
            //     var y = 0;

            //     for (uint i = 0; i < 100; ++i)
            //     {
            //         ref readonly var artInfo = ref arts.Value.GetArt(i + 1000);

            //         if (x > maxX)
            //         {
            //             x = 0;
            //             y += maxY;
            //         }

            //         world.Entity()
            //             .Set(new Renderable() {
            //                 Texture = artInfo.Texture,
            //                 Position = { X = x, Y = y },
            //                 Color = Vector3.UnitZ,
            //                 UV = artInfo.UV
            //             });

            //         x += artInfo.UV.Width;
            //         maxY = Math.Max(maxY, artInfo.UV.Height);
            //     }

            // }, Stages.Startup);

            scheduler.AddSystem(static (TinyEcs.World world, Res<Assets.MapLoader> mapLoader, Res<Renderer.Arts.Art> arts, Res<GraphicsDevice> device) => {

                static Vector2 IsoToScreen(ushort isoX, ushort isoY, sbyte isoZ, Vector2 centerOffset)
                {
                    return new Vector2(
                        ((isoX - isoY) * 22) - centerOffset.X - 22,
                        ((isoX + isoY) * 22 - (isoZ << 2)) - centerOffset.Y - 22
                    );
                }

                var centerChunkX = 1421 / 8;
                var centerChunkY = 1699 / 8;
                var offset = 4;
                var center = IsoToScreen((ushort)(centerChunkX * 8), (ushort) (centerChunkY * 8), 0, Vector2.Zero);
                center.X -= device.Value.PresentationParameters.BackBufferWidth / 2 - 22;
                center.Y -= device.Value.PresentationParameters.BackBufferHeight / 2 - 22;

                for (var chunkX = centerChunkX - offset; chunkX < centerChunkX + offset; ++chunkX)
                {
                    for (var chunkY = centerChunkY - offset; chunkY < centerChunkY + offset; ++chunkY)
                    {
                        ref var im = ref mapLoader.Value.GetIndex(0, chunkX, chunkY);

                        if (im.MapAddress == 0)
                            return;

                        var block = (Assets.MapBlock*) im.MapAddress;
                        var cells = (Assets.MapCells*) &block->Cells;
                        var bx = chunkX << 3;
                        var by = chunkY << 3;

                        for (int y = 0; y < 8; ++y)
                        {
                            var pos = y << 3;
                            var tileY = (ushort) (by + y);

                            for (int x = 0; x < 8; ++x, ++pos)
                            {
                                var tileID = (ushort) (cells[pos].TileID & 0x3FFF);
                                var z = cells[pos].Z;
                                var tileX = (ushort) (bx + x);

                                ref readonly var artInfo = ref arts.Value.GetLand(tileID);

                                world.Entity()
                                    .Set(new Renderable() {
                                        Texture = artInfo.Texture,
                                        UV = artInfo.UV,
                                        Color = Vector3.UnitZ,
                                        Position = IsoToScreen(tileX, tileY, z, center)
                                    });
                            }
                        }

                        if (im.StaticAddress != 0)
                        {
                            var sb = (Assets.StaticsBlock*) im.StaticAddress;

                            if (sb != null)
                            {
                                for (int i = 0, count = (int) im.StaticCount; i < count; ++i, ++sb)
                                {
                                    if (sb->Color != 0 && sb->Color != 0xFFFF)
                                    {
                                        int pos = (sb->Y << 3) + sb->X;

                                        if (pos >= 64)
                                        {
                                            continue;
                                        }

                                        var staX = (ushort)(bx + sb->X);
                                        var staY = (ushort)(by + sb->Y);

                                        ref readonly var artInfo = ref arts.Value.GetArt(sb->Color);

                                        world.Entity()
                                            .Set(new Renderable() {
                                                Texture = artInfo.Texture,
                                                UV = artInfo.UV,
                                                Color = Renderer.ShaderHueTranslator.GetHueVector(sb->Hue),
                                                Position = IsoToScreen(staX, staY, sb->Z, center)
                                            });
                                    }
                                }
                            }
                        }
                    }
                }

            }, Stages.Startup);
        }
    }

    struct FnaPlugin : IPlugin
    {
        public bool WindowResizable { get; set; }
        public bool MouseVisible { get; set; }
        public bool VSync { get; set; }



        public void Build(Scheduler scheduler)
        {
            var game = new UoGame(MouseVisible, WindowResizable, VSync);
            scheduler.AddResource(game);
            scheduler.AddResource(Keyboard.GetState());
            scheduler.AddResource(Mouse.GetState());
            scheduler.AddEvent<KeyEvent>();
            scheduler.AddEvent<MouseEvent>();
            scheduler.AddEvent<WheelEvent>();

            scheduler.AddSystem((Res<UoGame> game, SchedulerState schedState) => {
                game.Value.BeforeLoop();
                game.Value.RunOneFrame();
                schedState.AddResource(game.Value.GraphicsDevice);
                game.Value.RunApplication = true;
            }, Stages.Startup);

            scheduler.AddSystem((Res<UoGame> game) => {
                game.Value.SuppressDraw();
                game.Value.Tick();

                FrameworkDispatcher.Update();
            }).RunIf((SchedulerState state) => state.ResourceExists<UoGame>());

            scheduler.AddSystem((Res<UoGame> game) => {
                Environment.Exit(0);
            }, Stages.AfterUpdate).RunIf((Res<UoGame> game) => !game.Value.RunApplication);

            scheduler.AddSystem((Res<GraphicsDevice> device, Res<Renderer.UltimaBatcher2D> batch, Query<Renderable> query) => {
                device.Value.Clear(Color.AliceBlue);

                var sb = batch.Value;
                sb.Begin();
                query.Each((ref Renderable renderable) =>
                    sb.Draw
                    (
                        renderable.Texture,
                        renderable.Position,
                        renderable.UV,
                        renderable.Color
                    )
                );
                sb.End();
                device.Value.Present();
            }, Stages.AfterUpdate).RunIf((SchedulerState state) => state.ResourceExists<GraphicsDevice>());

            scheduler.AddSystem((EventWriter<KeyEvent> writer, Res<KeyboardState> oldState) => {
                var newState = Keyboard.GetState();

                foreach (var key in oldState.Value.GetPressedKeys())
                    if (newState.IsKeyUp(key)) // [pressed] -> [released]
                        writer.Enqueue(new KeyEvent() { Action = 0, Key = key });

                foreach (var key in newState.GetPressedKeys())
                    if (oldState.Value.IsKeyUp(key)) // [released] -> [pressed]
                        writer.Enqueue(new KeyEvent() { Action = 1, Key = key });
                    else if (oldState.Value.IsKeyDown(key))
                        writer.Enqueue(new KeyEvent() { Action = 2, Key = key });

                oldState.Value = newState;
            });

            scheduler.AddSystem((EventWriter<MouseEvent> writer, EventWriter<WheelEvent> wheelWriter, Res<MouseState> oldState) => {
                var newState = Mouse.GetState();

                if (newState.LeftButton != oldState.Value.LeftButton)
                    writer.Enqueue(new MouseEvent() { Action = newState.LeftButton, Button = Input.MouseButtonType.Left, X = newState.X, Y = newState.Y });
                if (newState.RightButton != oldState.Value.RightButton)
                    writer.Enqueue(new MouseEvent() { Action = newState.RightButton, Button = Input.MouseButtonType.Right, X = newState.X, Y = newState.Y });
                if (newState.MiddleButton != oldState.Value.MiddleButton)
                    writer.Enqueue(new MouseEvent() { Action = newState.MiddleButton, Button = Input.MouseButtonType.Middle, X = newState.X, Y = newState.Y });
                if (newState.XButton1 != oldState.Value.XButton1)
                    writer.Enqueue(new MouseEvent() { Action = newState.XButton1, Button = Input.MouseButtonType.XButton1, X = newState.X, Y = newState.Y });
                if (newState.XButton2 != oldState.Value.XButton2)
                    writer.Enqueue(new MouseEvent() { Action = newState.XButton2, Button = Input.MouseButtonType.XButton2, X = newState.X, Y = newState.Y });

                if (newState.ScrollWheelValue != oldState.Value.ScrollWheelValue)
                    // FNA multiplies for 120 for some reason
                    wheelWriter.Enqueue(new WheelEvent() { Value = (oldState.Value.ScrollWheelValue - newState.ScrollWheelValue) / 120 });

                oldState.Value = newState;
            });

            scheduler.AddSystem((EventReader<KeyEvent> reader) => {
                foreach (var ev in reader.Read())
                    Console.WriteLine("key {0} is {1}", ev.Key, ev.Action switch {
                        0 => "up",
                        1 => "down",
                        2 => "pressed",
                        _ => "unkown"
                    });
            });

            scheduler.AddSystem((EventReader<MouseEvent> reader) => {
                foreach (var ev in reader.Read())
                    Console.WriteLine("mouse button {0} is {1} at {2},{3}", ev.Button, ev.Action switch {
                        ButtonState.Pressed => "pressed",
                        ButtonState.Released => "released",
                        _ => "unknown"
                    }, ev.X, ev.Y);
            }).RunIf((Res<UoGame> game) => game.Value.IsActive);

            scheduler.AddSystem((EventReader<WheelEvent> reader) => {
                foreach (var ev in reader.Read())
                    Console.WriteLine("wheel value {0}", ev.Value);
            }).RunIf((Res<UoGame> game) => game.Value.IsActive);
        }

        struct KeyEvent
        {
            public byte Action;
            public Keys Key;
        }

        struct MouseEvent
        {
            public ButtonState Action;
            public Input.MouseButtonType Button;
            public int X, Y;
        }

        struct WheelEvent
        {
            public int Value;
        }

        sealed class UoGame : Microsoft.Xna.Framework.Game
        {
            public UoGame(bool mouseVisible, bool allowWindowResizing, bool vSync)
            {
                GraphicManager = new GraphicsDeviceManager(this)
                {
                    SynchronizeWithVerticalRetrace = vSync
                };
                IsFixedTimeStep = false;
                IsMouseVisible = mouseVisible;
                Window.AllowUserResizing = allowWindowResizing;
            }

            public GraphicsDeviceManager GraphicManager { get; }


            protected override void Initialize()
            {
                base.Initialize();
            }

            protected override void LoadContent()
            {
                base.LoadContent();
            }

            protected override void Update(GameTime gameTime)
            {
                // I don't want to update things here, but on ecs systems instead
            }

            protected override void Draw(GameTime gameTime)
            {
                // I don't want to render things here, but on ecs systems instead
            }
        }
    }
}