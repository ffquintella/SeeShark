// Copyright (c) Speykious
// This file is part of SeeShark.
// SeeShark is licensed under the BSD 3-Clause License. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using SeeShark.Device;
using SeeShark.FFmpeg;
using static SeeShark.FFmpeg.FFmpegManager;

namespace SeeShark.Decode;

/// <summary>
/// Decodes a video stream. <br/>
/// Based on https://github.com/Ruslan-B/FFmpeg.AutoGen/blob/master/FFmpeg.AutoGen.Example/VideoStreamDecoder.cs.
/// </summary>
public unsafe class VideoStreamDecoder : Disposable
{
    protected readonly AVCodecContext* CodecContext;
    protected readonly AVFormatContext* FormatContext;
    protected readonly Frame Frame;
    protected readonly AVPacket* Packet;
    protected readonly AVStream* Stream;
    protected readonly int StreamIndex;

    public readonly string CodecName;
    public readonly int FrameWidth;
    public readonly int FrameHeight;
    public readonly PixelFormat PixelFormat;
    public AVRational Framerate => Stream->r_frame_rate;

    private bool isFormatContextOpen = false;

    public VideoStreamDecoder(string url, DeviceInputFormat inputFormat, IDictionary<string, string>? options = null)
        : this(url, ffmpeg.av_find_input_format(inputFormat.ToString()), options)
    {
    }

    public VideoStreamDecoder(string url, AVInputFormat* inputFormat = null, IDictionary<string, string>? options = null)
    {
        SetupFFmpeg();

        FormatContext = ffmpeg.avformat_alloc_context();
        FormatContext->flags = ffmpeg.AVFMT_FLAG_NONBLOCK;

        var formatContext = FormatContext;

        AVDictionary* dict = null;

        if (options != null)
        {
            foreach (KeyValuePair<string, string> pair in options)
                ffmpeg.av_dict_set(&dict, pair.Key, pair.Value, 0);
        }

        // We are defining these defaulst for now
        /*
        ffmpeg.av_dict_set(&dict, "video_size", "640x480", 0);
        ffmpeg.av_dict_set(&dict, "framerate", "20", 0);
        ffmpeg.av_dict_set(&dict, "pixel_format", "rgb24", 0);
        */

        /*AVInputFormat *ifmt = ffmpeg.av_find_input_format("avfoundation");
        var pFormatCtx = ffmpeg.avformat_alloc_context();

        int openInputErr = ffmpeg.avformat_open_input(&pFormatCtx, "0", ifmt, &dict);*/

        int openInputErr = ffmpeg.avformat_open_input(&formatContext, url, inputFormat, &dict);


        ffmpeg.av_dict_free(&dict);
        openInputErr.ThrowExceptionIfError();
        isFormatContextOpen = true;

        AVCodec* codec = null;
        StreamIndex = ffmpeg
            .av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0)
            .ThrowExceptionIfError();
        Stream = formatContext->streams[StreamIndex];
        CodecContext = ffmpeg.avcodec_alloc_context3(codec);

        ffmpeg.avcodec_parameters_to_context(CodecContext, Stream->codecpar)
            .ThrowExceptionIfError();
        ffmpeg.avcodec_open2(CodecContext, codec, null).ThrowExceptionIfError();

        CodecName = ffmpeg.avcodec_get_name(codec->id);
        FrameWidth = CodecContext->width;
        FrameHeight = CodecContext->height;
        PixelFormat = (PixelFormat)CodecContext->pix_fmt;

        Packet = ffmpeg.av_packet_alloc();
        Frame = new Frame();
    }

    public DecodeStatus TryDecodeNextFrame(out Frame nextFrame)
    {
        int eagain = ffmpeg.AVERROR(ffmpeg.EAGAIN);
        int error;

        do
        {
            #region Read frame

            // We need to sleep for a bit to avoid high CPU usage
            //Thread.Sleep(1);

            // Manually wait for a new frame instead of letting it block
            ffmpeg.av_packet_unref(Packet);
            error = ffmpeg.av_read_frame(FormatContext, Packet);

            if (error < 0)
            {
                nextFrame = Frame;
                GC.Collect();

                // We only wait longer once to make sure we catch the frame on time.
                return error == eagain
                    ? DecodeStatus.NoFrameAvailable
                    : DecodeStatus.EndOfStream;
            }

            error.ThrowExceptionIfError();
            #endregion

            #region Decode packet
            if (Packet->stream_index != StreamIndex)
                throw new InvalidOperationException("Packet does not belong to the decoder's video stream");

            ffmpeg.avcodec_send_packet(CodecContext, Packet).ThrowExceptionIfError();

            Frame.Unref();
            error = Frame.Receive(CodecContext);
            #endregion
        }
        while (error == eagain);
        error.ThrowExceptionIfError();

        nextFrame = Frame;
        GC.Collect();
        return DecodeStatus.NewFrame;
    }

    public IReadOnlyDictionary<string, string> GetContextInfo()
    {
        AVDictionaryEntry* tag = null;
        var result = new Dictionary<string, string>();

        while ((tag = ffmpeg.av_dict_get(FormatContext->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            var key = Marshal.PtrToStringAnsi((IntPtr)tag->key);
            var value = Marshal.PtrToStringAnsi((IntPtr)tag->value);

            if (key != null && value != null)
                result.Add(key, value);
        }

        return result;
    }

    protected override void DisposeManaged()
    {
        Frame.Dispose();
    }

    protected override void DisposeUnmanaged()
    {
        // Constructor initialization can fail at some points,
        // so we need to null check everything.
        // See https://github.com/vignetteapp/SeeShark/issues/27

        if (CodecContext != null && ffmpeg.avcodec_is_open(CodecContext) > 0)
            ffmpeg.avcodec_close(CodecContext);

        if (FormatContext != null && isFormatContextOpen)
        {
            AVFormatContext* formatContext = FormatContext;
            ffmpeg.avformat_close_input(&formatContext);
        }

        if (Packet != null)
        {
            AVPacket* packet = Packet;
            ffmpeg.av_packet_free(&packet);
        }
    }
}
