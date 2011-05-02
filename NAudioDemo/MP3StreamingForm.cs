﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NAudio.Wave;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.IO;
using System.Diagnostics;

namespace NAudioDemo
{
    public partial class MP3StreamingForm : Form
    {
        enum StreamingPlaybackState
        {
            Stopped,
            Playing,
            Buffering,
            Paused
        }

        public MP3StreamingForm()
        {
            InitializeComponent();
        }

        BufferedWaveProvider bufferedWaveProvider;
        IWavePlayer waveOut;
        volatile StreamingPlaybackState playbackState;
        volatile bool fullyDownloaded;

        delegate void ShowErrorDelegate(string message);

        private void ShowError(string message)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new ShowErrorDelegate(ShowError), message);
            }
            else
            {
                MessageBox.Show(message);
            }
        }

        private void StreamMP3(object state)
        {
            this.fullyDownloaded = false;
            string url = (string)state;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            HttpWebResponse resp = null;
            try
            { 
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch(WebException e)
            {
                ShowError(e.Message);
                return;
            }
            byte[] buffer = new byte[16384 * 2];

            IMp3FrameDecompressor decompressor = null;
            try
            {
                using (var responseStream = resp.GetResponseStream())
                {
                    var readFullyStream = new ReadFullyStream(responseStream);
                    do
                    {
                        if (bufferedWaveProvider != null && bufferedWaveProvider.BufferLength - bufferedWaveProvider.BufferedBytes < bufferedWaveProvider.WaveFormat.AverageBytesPerSecond / 4)
                        {
                            Debug.WriteLine("Buffer getting full, taking a break");
                            Thread.Sleep(500);
                        }
                        else
                        {
                            Mp3Frame frame = null;
                            try
                            {
                                frame = new Mp3Frame(readFullyStream, true);
                            }
                            catch (EndOfStreamException e)
                            {
                                this.fullyDownloaded = true;
                                // reached the end of the MP3 file / stream
                                break;
                            }
                            if (decompressor == null)
                            {
                                // don't think these details matter too much - just help ACM select the right codec
                                // however, the buffered provider doesn't know what sample rate it is working at
                                // until we have a frame
                                WaveFormat waveFormat = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2, frame.FrameLength, frame.BitRate);
                                decompressor = new AcmMp3FrameDecompressor(waveFormat);
                                this.bufferedWaveProvider = new BufferedWaveProvider(decompressor.OutputFormat);
                                this.bufferedWaveProvider.BufferDuration = TimeSpan.FromSeconds(20); // allow us to get well ahead of ourselves
                                //this.bufferedWaveProvider.BufferedDuration = 250;
                            }
                            int decompressed = decompressor.DecompressFrame(frame, buffer, 0);
                            //Debug.WriteLine(String.Format("Decompressed a frame {0}", decompressed));
                            bufferedWaveProvider.AddSamples(buffer, 0, decompressed);
                        }

                    } while (playbackState != StreamingPlaybackState.Stopped);
                    Debug.WriteLine("Exiting");
                    // was doing this in a finally block, but for some reason
                    // we are hanging on response stream .Dispose so never get there
                    decompressor.Dispose();
                }
            }
            finally
            {
                if (decompressor != null)
                {
                    decompressor.Dispose();
                }
            }
        }

        private void buttonPlay_Click(object sender, EventArgs e)
        {
            if (playbackState == StreamingPlaybackState.Stopped)
            {
                playbackState = StreamingPlaybackState.Buffering;
                this.bufferedWaveProvider = null;
                ThreadPool.QueueUserWorkItem(new WaitCallback(StreamMP3), textBoxStreamingUrl.Text);
                timer1.Enabled = true;
            }
            else if (playbackState == StreamingPlaybackState.Paused)
            {
                playbackState = StreamingPlaybackState.Buffering;
            }
        }

        private void StopPlayback()
        {
            if (playbackState != StreamingPlaybackState.Stopped)
            {
                this.playbackState = StreamingPlaybackState.Stopped;
                if (waveOut != null)
                { 
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }
                timer1.Enabled = false;
                // n.b. streaming thread may not yet have exited
                Thread.Sleep(500);
                ShowBufferState(0);
            }
        }

        private void ShowBufferState(double totalSeconds)
        {
            labelBuffered.Text = String.Format("{0:0.0}s", totalSeconds);
            progressBarBuffer.Value = (int)(totalSeconds * 1000);
        }



        private void timer1_Tick(object sender, EventArgs e)
        {
            if (playbackState != StreamingPlaybackState.Stopped)
            {
                if (this.waveOut == null && this.bufferedWaveProvider != null)
                {
                    Debug.WriteLine("Creating WaveOut Device");
                    this.waveOut = CreateWaveOut(); 
                    waveOut.PlaybackStopped += new EventHandler(waveOut_PlaybackStopped);
                    waveOut.Init(bufferedWaveProvider);
                    progressBarBuffer.Maximum = (int)bufferedWaveProvider.BufferDuration.TotalMilliseconds;
                }
                else if (bufferedWaveProvider != null)
                {
                    var bufferedSeconds = bufferedWaveProvider.BufferedDuration.TotalSeconds;
                    ShowBufferState(bufferedSeconds);
                    // make it stutter less if we buffer up a decent amount before playing
                    if (bufferedSeconds < 0.5 && this.playbackState == StreamingPlaybackState.Playing && !this.fullyDownloaded)
                    {
                        this.playbackState = StreamingPlaybackState.Buffering;
                        waveOut.Pause();
                        Debug.WriteLine(String.Format("Paused to buffer, waveOut.PlaybackState={0}", waveOut.PlaybackState));
                    }
                    else if (bufferedSeconds > 4 && this.playbackState == StreamingPlaybackState.Buffering)
                    {
                        waveOut.Play();
                        Debug.WriteLine(String.Format("Started playing, waveOut.PlaybackState={0}", waveOut.PlaybackState));
                        this.playbackState = StreamingPlaybackState.Playing;
                    }
                    else if (this.fullyDownloaded && bufferedSeconds == 0)
                    {
                        Debug.WriteLine("Reached end of stream");
                        StopPlayback();
                    }
                }
            }
        }

        private IWavePlayer CreateWaveOut()
        {
            return new WaveOut();
            //return new DirectSoundOut();
        }

        private void MP3StreamingForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopPlayback();
        }

        private void buttonPause_Click(object sender, EventArgs e)
        {
            if (playbackState == StreamingPlaybackState.Playing || playbackState == StreamingPlaybackState.Buffering)
            {
                waveOut.Pause();
                Debug.WriteLine(String.Format("User requested Pause, waveOut.PlaybackState={0}", waveOut.PlaybackState));
                playbackState = StreamingPlaybackState.Paused;
            }
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            StopPlayback();
        }

        private void waveOut_PlaybackStopped(object sender, EventArgs e)
        {
            Debug.WriteLine("Playback Stopped");
        }
    }
}
