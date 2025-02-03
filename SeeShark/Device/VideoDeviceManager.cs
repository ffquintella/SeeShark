// Copyright (c) Speykious
// This file is part of SeeShark.
// SeeShark is licensed under the BSD 3-Clause License. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Native;
using SeeShark.FFmpeg;
using static SeeShark.FFmpeg.FFmpegManager;

namespace SeeShark.Device;

/// <summary>
/// Manages your video devices. Is able to enumerate them and create new <see cref="T"/>s.
/// It can also watch for available devices, and fire up <see cref="OnNewDevice"/> and
/// <see cref="OnLostDevice"/> events when it happens.
/// </summary>
public abstract unsafe class VideoDeviceManager<TDeviceInfo, T> : Disposable
    where T : VideoDevice
    where TDeviceInfo : VideoDeviceInfo, new()
{
    protected readonly AVInputFormat* AvInputFormat;
    protected readonly AVOutputFormat* avOutputFormat;
    protected readonly AVFormatContext* AvFormatContext;
    protected Timer DeviceWatcher;

    /// <summary>
    /// Whether this <see cref="VideoDeviceManager"/> is watching for devices.
    /// </summary>
    public bool IsWatching { get; protected set; }

    /// <summary>
    /// Input format used by this <see cref="VideoDeviceManager"/> to watch devices.
    /// </summary>
    public DeviceInputFormat InputFormat { get; protected set; }

    /// <summary>
    /// List of all the available video devices.
    /// </summary>
    public ImmutableList<TDeviceInfo> Devices { get; protected set; } = ImmutableList<TDeviceInfo>.Empty;

    /// <summary>
    /// Invoked when a video device has been connected.
    /// </summary>
    public event Action<TDeviceInfo>? OnNewDevice;

    /// <summary>
    /// Invoked when a video device has been disconnected.
    /// </summary>
    public event Action<TDeviceInfo>? OnLostDevice;

    protected VideoDeviceManager(DeviceInputFormat inputFormat)
    {
        SetupFFmpeg();


        InputFormat = inputFormat;
        //AvInputFormat = ffmpeg.av_find_input_format(InputFormat.ToString());

        var inputStr = inputFormat.ToString();

        var inF = ffmpeg.av_find_input_format(inputStr);

        AvInputFormat = inF;


        //var outputFormat = new AVOutputFormat();
        //avOutputFormat = &outputFormat;

        AvFormatContext = ffmpeg.avformat_alloc_context();
        if (AvFormatContext == null)
            throw new InvalidOperationException("Failed to allocate AVFormatContext.");

        AvFormatContext->iformat = AvInputFormat;
        //AvFormatContext->oformat = avOutputFormat;
        //AvFormatContext->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;



        SyncDevices();
        DeviceWatcher = new Timer(
            (_state) => SyncDevices(),
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan
        );

        IsWatching = false;
    }

    public abstract T GetDevice(TDeviceInfo info, VideoInputOptions? options = null);
    public T GetDevice(int index = 0, VideoInputOptions? options = null) =>
        GetDevice(Devices[index], options);
    public T GetDevice(string path, VideoInputOptions? options = null) =>
        GetDevice(Devices.First((ci) => ci.Path == path), options);

    /// <summary>
    /// Starts watching for available devices.
    /// </summary>
    public void StartWatching()
    {
        DeviceWatcher.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        IsWatching = true;
    }

    /// <summary>
    /// Stops watching for available devices.
    /// </summary>
    public void StopWatching()
    {
        DeviceWatcher.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        IsWatching = false;
    }

    protected IFunctionResolver GetFunctionResolver()
    {
        switch (GetPlatformId())
        {
            case PlatformID.MacOSX:
                return new MacFunctionResolver();
            case PlatformID.Unix:
                return new LinuxFunctionResolver();
            case PlatformID.Win32NT:
                return new WindowsFunctionResolver();
            default:
                throw new PlatformNotSupportedException();
        }
    }

    public static PlatformID GetPlatformId()
    {

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PlatformID.Win32NT;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PlatformID.Unix;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return PlatformID.MacOSX;
        throw new PlatformNotSupportedException();

    }

    protected virtual TDeviceInfo[] EnumerateDevices()
    {
        if (!System.OperatingSystem.IsMacOS())
        {
            AVDeviceInfoList* avDeviceInfoList = null;

            ffmpeg.avdevice_list_input_sources(AvInputFormat, null, null, &avDeviceInfoList).ThrowExceptionIfError();
            //ffmpeg.avdevice_list_devices(AvFormatContext, &avDeviceInfoList).ThrowExceptionIfError();

            int nDevices = avDeviceInfoList->nb_devices;
            AVDeviceInfo** avDevices = avDeviceInfoList->devices;

            TDeviceInfo[] devices = new TDeviceInfo[nDevices];
            for (int i = 0; i < nDevices; i++)
            {
                AVDeviceInfo* avDevice = avDevices[i];
                string name = new string((sbyte*)avDevice->device_description);
                string path = new string((sbyte*)avDevice->device_name);

                if (path == null)
                    throw new InvalidOperationException($"Device at index {i} doesn't have a path!");

                devices[i] = new TDeviceInfo
                {
                    Name = name,
                    Path = path,
                };
            }


            ffmpeg.avdevice_free_list_devices(&avDeviceInfoList);
            return devices;
        }
        else
        {
            var devices = ListMacDevices();
            return devices.ToArray();
        }

    }

    private List<TDeviceInfo> ListMacDevices()
    {
        var binPath = "/usr/bin/ffmpeg";
        if (ffmpeg.RootPath.EndsWith("lib/")) binPath = ffmpeg.RootPath.Replace("lib/", "bin/");
        if (ffmpeg.RootPath.EndsWith("lib")) binPath = ffmpeg.RootPath.Replace("lib", "bin/");

        var binFile = binPath + "ffmpeg";

        var args = " -f avfoundation -list_devices true -i \"\"";

        // Start the child process.
        Process p = new Process();
        // Redirect the output stream of the child process.
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.FileName = binFile;
        p.StartInfo.Arguments = args;
        p.StartInfo.WorkingDirectory = binPath;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.RedirectStandardOutput = true;
        var stdOutput = new StringBuilder();
        p.OutputDataReceived += (sender, args) => stdOutput.AppendLine(args.Data); // Use AppendLine rather than Append since args.Data is one line of output, not including the newline character.


        string stdError = null;

        string result = "";
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            stdError = p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        catch (Exception e)
        {
            throw new Exception("OS error while executing " + Format(binFile, args)+ ": " + e.Message, e);
        }

        // we work with stderr because ffmpeg works this way
        var message = new StringBuilder();

        if (!string.IsNullOrEmpty(stdError))
        {
            message.AppendLine(stdError);
        }



        var lines = message.ToString().Split("\n");


        List<TDeviceInfo> devices = new List<TDeviceInfo>();

        foreach (var line in lines)
        {
            if(line.StartsWith("[AVFoundation"))
            {
                var device = new TDeviceInfo();
                var parts = line.Split("]");
                if(parts.Length != 3)
                {
                    if (parts[1].Trim() == "AVFoundation audio devices:")
                        break;
                    continue;
                }
                device.Name = parts[2].Trim();
                device.Path = parts[1].Replace("[", "").Trim() ;
                devices.Add(device);
            }
        }




        return devices;
    }

    private string Format(string filename, string arguments)
    {
        return "'" + filename +
               ((string.IsNullOrEmpty(arguments)) ? string.Empty : " " + arguments) +
               "'";
    }

    /// <summary>
    /// Looks for available devices and triggers <see cref="OnNewDevice"/> and <see cref="OnLostDevice"/> events.
    /// </summary>
    public void SyncDevices()
    {
        ImmutableList<TDeviceInfo> newDevices = EnumerateDevices().ToImmutableList();

        if (Devices.SequenceEqual(newDevices))
            return;

        foreach (TDeviceInfo device in newDevices.Except(Devices))
            OnNewDevice?.Invoke(device);

        foreach (TDeviceInfo device in Devices.Except(newDevices))
            OnLostDevice?.Invoke(device);

        Devices = newDevices;
    }

    protected override void DisposeManaged()
    {
        DeviceWatcher.Dispose();
    }
}
