﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenUtau.Core.USTx;
using OpenUtau.Core.Render;
using OpenUtau.Core.ResamplerDriver;

namespace OpenUtau.Core
{
    class PlaybackManager : ICmdSubscriber
    {
        private WaveOut outDevice;
        private bool Export = false;
        private bool firstCheck = true;
        private PlaybackManager() { this.Subscribe(DocManager.Inst); }

        private static PlaybackManager _s;
        public static PlaybackManager Inst { get { if (_s == null) { _s = new PlaybackManager(); } return _s; } }

        MixingSampleProvider masterMix;
        List<TrackSampleProvider> trackSources;

        public bool CheckResampler() {
            var path = PathManager.Inst.GetPreviewEnginePath();
            Directory.CreateDirectory(PathManager.Inst.GetEngineSearchPath());
            return File.Exists(path); // TODO: validate exe / dll
        }

        public void Play(UProject project)
        {
            Export = false;
            if (pendingParts > 0) return;
            else if (outDevice != null)
            {
                if (outDevice.PlaybackState == PlaybackState.Playing) return;
                else if (outDevice.PlaybackState == PlaybackState.Paused) { outDevice.Play(); return; }
                else outDevice.Dispose();
            }
            BuildAudio(project);
        }

        public void StopPlayback()
        {
            if (outDevice != null) outDevice.Stop();
        }

        public void PausePlayback()
        {
            if (outDevice != null) outDevice.Pause();
        }

        private void StartPlayback()
        {
            masterMix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            foreach (var source in trackSources) {
                
                    masterMix.AddMixerInput(source);
                
            }
            outDevice = new WaveOut();
            outDevice.Init(masterMix);
            outDevice.Play();
        }

        private ISampleProvider BuildWavePartAudio(UWavePart part, UProject project)
        {
            AudioFileReader stream;
            try { stream = new AudioFileReader(part.FilePath); }
            catch { return null; }
            return new WaveToSampleProvider(stream);
        }

        private void BuildVoicePartAudio(UVoicePart part, UProject project,IResamplerDriver engine)
        {
            ResamplerInterface ri = new ResamplerInterface();
            ri.ResamplePart(part, project, engine, (o) => { this.BuildVoicePartDone(o, part, project); });
        }

        private void BuildVoicePartDone(SequencingSampleProvider source, UPart part, UProject project) {
            lock (lockObject) {
                if (source != null) {
                    trackSources[part.TrackNo].AddSource(
                        source,
                        TimeSpan.FromMilliseconds(project.TickToMillisecond(part.PosTick))
                    );
                }
                pendingParts--;
            }
            if (pendingParts == 0) {
                if (!Export) {
                    StartPlayback();
                } else {
                    ExportAudio();
                }
            }
                
        }

        int pendingParts = 0;
        private readonly object lockObject = new object();

        private void BuildAudio(UProject project)
        {
            trackSources = new List<TrackSampleProvider>();
            foreach (UTrack track in project.Tracks)
            {
                trackSources.Add(new TrackSampleProvider() { Volume = DecibelToVolume(track.Volume), Pan = track.Pan });
            }
            pendingParts = project.Parts.Count;
            foreach (UPart part in project.Parts)
            {
                if (part is UWavePart)
                {
                    lock (lockObject)
                    {
                        trackSources[part.TrackNo].AddSource(
                            BuildWavePartAudio(part as UWavePart, project),
                            TimeSpan.FromMilliseconds(project.TickToMillisecond(part.PosTick))
                        );
                        pendingParts--;
                    }
                }
                else
                {
                    var singer = project.Tracks[part.TrackNo].Singer;
                    if (singer != null && singer.Loaded)
                    {
                        FileInfo ResamplerFile = new FileInfo(PathManager.Inst.GetPreviewEnginePath());
                        IResamplerDriver engine = ResamplerDriver.ResamplerDriver.LoadEngine(ResamplerFile.FullName);
                        BuildVoicePartAudio(part as UVoicePart, project, engine);
                    }
                    else lock (lockObject) { pendingParts--; }
                }
            }

            if (pendingParts == 0) {
                StartPlayback();
            }
        }

        public void UpdatePlayPos()
        {
            if (outDevice != null && outDevice.PlaybackState == PlaybackState.Playing)
            {
                double ms = outDevice.GetPosition() * 1000.0 / masterMix.WaveFormat.BitsPerSample /masterMix.WaveFormat.Channels * 8 / masterMix.WaveFormat.SampleRate;
                int tick = DocManager.Inst.Project.MillisecondToTick(ms);
                DocManager.Inst.ExecuteCmd(new SetPlayPosTickNotification(tick), true);
            }
        }

        private float DecibelToVolume(double db)
        {
            return (db == -24) ? 0 : (float)((db < -16) ? MusicMath.DecibelToLinear(db * 2 + 16) : MusicMath.DecibelToLinear(db));
        }

        public void ExportAudio() {
            Export = true;
            if (firstCheck) {
                BuildAudio(DocManager.Inst.Project);
                firstCheck = false;
            } else if (pendingParts == 0) {
                masterMix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
                foreach (var source in trackSources)
                    masterMix.AddMixerInput(source);
                WaveFileWriter.CreateWaveFile("Export.wav", masterMix.ToWaveProvider());
            }
        }
        
        # region ICmdSubscriber

        public void Subscribe(ICmdPublisher publisher) { if (publisher != null) publisher.Subscribe(this); }

        public void OnNext(UCommand cmd, bool isUndo)
        {
            if (cmd is SeekPlayPosTickNotification) {
                StopPlayback();
                int tick = ((SeekPlayPosTickNotification)cmd).playPosTick;
                DocManager.Inst.ExecuteCmd(new SetPlayPosTickNotification(tick));
            } 
            else if (cmd is VolumeChangeNotification) {
                var _cmd = cmd as VolumeChangeNotification;
                if (trackSources != null && trackSources.Count > _cmd.TrackNo) {
                    trackSources[_cmd.TrackNo].Volume = DecibelToVolume(_cmd.Volume);
                }
            } 
            else if (cmd is TrackMuteNotification) {
                var _cmd = cmd as TrackMuteNotification;
                if (trackSources != null && trackSources.Count > _cmd.TrackNo && _cmd.Toggle) {
                    trackSources[_cmd.TrackNo].Volume = 0f;
                } else if (trackSources != null && trackSources.Count > _cmd.TrackNo && !_cmd.Toggle) {
                    trackSources[_cmd.TrackNo].Volume = DecibelToVolume(_cmd.Volume);
                }
            } 
            else if (cmd is TrackSoloNotification) {
                var _cmd = cmd as TrackSoloNotification;
                if (trackSources != null && trackSources.Count > _cmd.TrackNo && _cmd.Toggle) {
                    for (int i = 0; i < trackSources.Count; i++) {
                        if (i == _cmd.TrackNo) {
                            continue;
                        } else {
                            DocManager.Inst.ExecuteCmd(new TrackMuteNotification(i, true, DocManager.Inst.Project.Tracks[i].Volume));
                        }
                    }
                } else if (trackSources != null && trackSources.Count > _cmd.TrackNo && !_cmd.Toggle) {
                    for (int i = 0; i < trackSources.Count; i++) {
                        if (i == _cmd.TrackNo) {
                            continue;
                        } else {
                            DocManager.Inst.ExecuteCmd(new TrackMuteNotification(i, false, DocManager.Inst.Project.Tracks[i].Volume));
                        }
                    }
                }
            }
            else if (cmd is PanChangeNotification) {
                var _cmd = cmd as PanChangeNotification;
                if(trackSources != null && trackSources.Count > _cmd.TrackNo) {
                    trackSources[_cmd.TrackNo].Pan = _cmd.Pan;
                }
            }
            
        }

        # endregion
    }
}
