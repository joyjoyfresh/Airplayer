using System;

namespace AirPlayer.Protocol.Models
{
    /// <summary>
    /// H264 视频帧数据结构
    /// </summary>
    public struct H264Data
    {
        /// <summary>H264 帧类型（如 5=IDR 关键帧）</summary>
        public int FrameType { get; set; }
        /// <summary>H264 NAL 数据（Annex B 格式）</summary>
        public byte[] Data { get; set; }
        /// <summary>数据有效长度</summary>
        public int Length { get; set; }
        /// <summary>PTS 时间戳（微秒）</summary>
        public long Pts { get; set; }
        /// <summary>画面宽度</summary>
        public int Width { get; set; }
        /// <summary>画面高度</summary>
        public int Height { get; set; }
    }
}
