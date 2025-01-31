// Copyright (c) Speykious
// This file is part of SeeShark.
// SeeShark is licensed under the BSD 3-Clause License. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Native;
using LF = SeeShark.FFmpeg.LibraryFlags;
//using LibraryLoader = FFmpeg.AutoGen.Native.LibraryLoader;



namespace SeeShark.FFmpeg;

public static class FFmpegManager_old
{
    public static bool IsFFmpegSetup { get; private set; } = false;
    public static bool LogLibrarySearch { get; set; } = true; // Enable logging

    private static void llsLog(string message)
    {
        if (LogLibrarySearch)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Error.WriteLine(message);
            Console.ResetColor();
        }
    }

    public static string FFmpegVersion
    {
        get
        {
            SetupFFmpeg();
            return ffmpeg.av_version_info();
        }
    }

    public static string FFmpegRootPath
    {
        get => ffmpeg.RootPath;
        private set => ffmpeg.RootPath = value;
    }

    private static av_log_set_callback_callback? logCallback;

    public static void SetupFFmpeg(FFmpegLogLevel logLevel, ConsoleColor logColor, params string[] paths)
    {
        if (IsFFmpegSetup)
            return;

        llsLog("Setting up FFmpeg\nRequired libraries:" +
            $"\n  - avcodec (v{ffmpeg.LIBAVCODEC_VERSION_MAJOR})" +
            $"\n  - avdevice (v{ffmpeg.LIBAVDEVICE_VERSION_MAJOR})" +
            $"\n  - avformat (v{ffmpeg.LIBAVFORMAT_VERSION_MAJOR})" +
            $"\n  - swscale (v{ffmpeg.LIBSWSCALE_VERSION_MAJOR})");

        var requiredLibs = LF.AVCodec | LF.AVDevice | LF.AVFormat | LF.SWScale;

        if (paths.Length == 0)
            TrySetRootPath(requiredLibs, AppDomain.CurrentDomain.BaseDirectory);
        else
            TrySetRootPath(requiredLibs, paths);
        SetupFFmpegLogging(logLevel, logColor);
        ffmpeg.avdevice_register_all();

        IsFFmpegSetup = true;
    }

    public static void SetupFFmpeg(params string[] paths) => SetupFFmpeg(FFmpegLogLevel.Panic, ConsoleColor.Yellow, paths);

    internal static unsafe void SetupFFmpegLogging(FFmpegLogLevel logLevel, ConsoleColor logColor)
    {
        ffmpeg.av_log_set_level((int)logLevel);

        logCallback = (p0, level, format, vl) =>
        {
            if (level > ffmpeg.av_log_get_level())
                return;

            var lineSize = 1024;
            var lineBuffer = stackalloc byte[lineSize];
            var printPrefix = 1;

            ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
            var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);

            Console.ForegroundColor = logColor;
            Console.Write(line);
            Console.ResetColor();
        };

        ffmpeg.av_log_set_callback(logCallback);
    }

    public static void TrySetRootPath(params string[] paths) => TrySetRootPath(paths);

    public static void TrySetRootPath(LF requiredLibraries, params string[] paths)
    {
        try
        {
            ffmpeg.RootPath = paths.First((path) => CanLoadLibraries(requiredLibraries, path));
        }
        catch (InvalidOperationException)
        {
            string pathList = "\n  - " + string.Join("\n  - ", paths);
            throw new InvalidOperationException(
                $"Couldn't find native libraries in the following paths:{pathList}" +
                "\nMake sure you installed the correct versions of the native libraries.");
        }
    }

    public static bool CanLoadLibraries(LF libraries = LF.All, string path = "")
    {
        var validated = new List<string>();
        llsLog($"Searching for libraries in {path}");
        return libraries.ToStrings().All((lib) => canLoadLibrary(lib, path, validated));
    }

    private static bool canLoadLibrary(string lib, string path, List<string> validated)
    {
        if (validated.Contains(lib))
            return true;

        int version = ffmpeg.LibraryVersionMap[lib];
        if (!canLoadNativeLibrary(path, lib, version))
            return false;

        validated.Add(lib);
        return true;
    }

    private static bool canLoadNativeLibrary(string path, string libraryName, int version)
    {
        string nativeLibraryName = LibraryLoader.GetNativeLibraryName(libraryName, version);
        string fullName = Path.Combine(path, nativeLibraryName);
        bool exists = File.Exists(fullName);
        llsLog($"  {(exists ? "Found" : "Couldn't find")} library {nativeLibraryName}");
        return exists;
    }
}

