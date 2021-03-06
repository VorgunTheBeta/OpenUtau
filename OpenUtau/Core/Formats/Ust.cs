﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Serilog;

using OpenUtau.Core.USTx;
using OpenUtau.SimpleHelpers;

namespace OpenUtau.Core.Formats
{
    public static class Ust
    {
        private enum UstVersion { Early, V1_0, V1_1, V1_2, Unknown };
        private enum UstBlock { Version, Setting, Note, Trackend, None };

        private const string versionTag = "[#VERSION]";
        private const string settingTag = "[#SETTING]";
        private const string endTag = "[#TRACKEND]";

        static public void Load(string[] files)
        {
            bool ustTracks = true;
            foreach (string file in files)
            {
                if (OpenUtau.Core.Formats.Formats.DetectProjectFormat(file) != Core.Formats.ProjectFormats.Ust) { ustTracks = false; break; }
            }

            if (!ustTracks)
            {
                DocManager.Inst.ExecuteCmd(new UserMessageNotification("Multiple files must be all Ust files"));
                return;
            }

            List<UProject> projects = new List<UProject>();
            foreach (string file in files)
            {
                projects.Add(Load(file));
            }

            double bpm = projects.First().BPM;
            UProject project = new UProject() { BPM = bpm, Name = "Merged Project", Saved = false };
            project.RegisterExpression(new IntExpression(null, "velocity", "VEL") { Data = 100, Min = 0, Max = 200 });
            project.RegisterExpression(new IntExpression(null, "volume", "VOL") { Data = 100, Min = 0, Max = 200 });
            project.RegisterExpression(new IntExpression(null, "gender", "GEN") { Data = 0, Min = -100, Max = 100 });
            project.RegisterExpression(new IntExpression(null, "lowpass", "LPF") { Data = 0, Min = 0, Max = 100 });
            project.RegisterExpression(new IntExpression(null, "highpass", "HPF") { Data = 0, Min = 0, Max = 100 });
            project.RegisterExpression(new IntExpression(null, "accent", "ACC") { Data = 100, Min = 0, Max = 200 });
            project.RegisterExpression(new IntExpression(null, "decay", "DEC") { Data = 0, Min = 0, Max = 100 });
            foreach (UProject p in projects)
            {
                var _track = p.Tracks[0];
                var _part = p.Parts[0];
                var _singer = p.Singers[0];
                _track.TrackNo = project.Tracks.Count;
                _part.TrackNo = _track.TrackNo;
                project.Tracks.Add(_track);
                project.Parts.Add(_part);
                project.Singers.Add(_singer);
            }

            if (project != null) DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
        }

        static public UProject Load(string file, Encoding encoding = null)
        {
            int currentNoteIndex = 0;
            UstVersion version = UstVersion.Early;
            UstBlock currentBlock = UstBlock.None;
            string[] lines;

            try
            {
                if (encoding == null) lines = File.ReadAllLines(file, FileEncoding.DetectFileEncoding(file));
                else lines = File.ReadAllLines(file, encoding);
            }
            catch (Exception e)
            {
                DocManager.Inst.ExecuteCmd(new UserMessageNotification(e.GetType().ToString() + "\n" + e.Message));
                return null;
            }

            UProject project = new UProject() { Resolution = 480, FilePath = file, Saved = false };
            project.RegisterExpression(new IntExpression(null, "velocity","VEL") { Data = 100, Min = 0, Max = 200});
            project.RegisterExpression(new IntExpression(null, "volume","VOL") { Data = 100, Min = 0, Max = 200});
            project.RegisterExpression(new IntExpression(null, "gender","GEN") { Data = 0, Min = -100, Max = 100});
            project.RegisterExpression(new IntExpression(null, "lowpass","LPF") { Data = 0, Min = 0, Max = 100});
            project.RegisterExpression(new IntExpression(null, "highpass", "HPF") { Data = 0, Min = 0, Max = 100 });
            project.RegisterExpression(new IntExpression(null, "accent", "ACC") { Data = 100, Min = 0, Max = 200 });
            project.RegisterExpression(new IntExpression(null, "decay", "DEC") { Data = 0, Min = 0, Max = 100 });

            var _track = new UTrack();
            project.Tracks.Add(_track);
            _track.TrackNo = 0;
            UVoicePart part = new UVoicePart() { TrackNo = 0, PosTick = 0 };
            project.Parts.Add(part);

            List<string> currentLines = new List<string>();
            int currentTick = 0;
            UNote currentNote = null;

            foreach (string line in lines)
            {
                if (line.Trim().StartsWith(@"[#") && line.Trim().EndsWith(@"]"))
                {
                    if (line.Equals(versionTag)) currentBlock = UstBlock.Version;
                    else if (line.Equals(settingTag)) currentBlock = UstBlock.Setting;
                    else
                    {
                        if (line.Equals(endTag)) currentBlock = UstBlock.Trackend;
                        else
                        {
                            try { currentNoteIndex = int.Parse(line.Replace("[#", string.Empty).Replace("]", string.Empty)); }
                            catch { DocManager.Inst.ExecuteCmd(new UserMessageNotification("Unknown ust format")); return null; }
                            currentBlock = UstBlock.Note;
                        }

                        if (currentLines.Count != 0)
                        {
                            currentNote = NoteFromUst(project.CreateNote(), currentLines, version);
                            Log.Warning(currentNote.ToString());
                            currentNote.PosTick = currentTick;
                            Log.Warning(currentNote.PosTick.ToString());
                            if (!currentNote.Lyric.Replace("R", string.Empty).Replace("r", string.Empty).Equals(string.Empty)) part.Notes.Add(currentNote);
                            currentTick += currentNote.DurTick;
                            currentLines.Clear();
                        }
                    }
                }
                else
                {
                    if (currentBlock == UstBlock.Version) {
                        if (line.StartsWith("UST Version"))
                        {
                            string v = line.Trim().Replace("UST Version", string.Empty);
                            switch (v)
                            {
                                case "1.0":
                                    version = UstVersion.V1_0;
                                    break;
                                case "1.1":
                                    version = UstVersion.V1_1;
                                    break;
                                case "1.2":
                                    version = UstVersion.V1_2;
                                    break;
                                default:
                                    version = UstVersion.Unknown;
                                    break;
                            }
                        }
                    }
                    if (currentBlock == UstBlock.Setting)
                    {
                        if (line.StartsWith("Tempo="))
                        {
                            project.BPM = double.Parse(line.Trim().Replace("Tempo=", string.Empty));
                            if (project.BPM == 0) project.BPM = 120;
                        }
                        if (line.StartsWith("ProjectName=")) project.Name = line.Trim().Replace("ProjectName=", string.Empty);
                        if (line.StartsWith("VoiceDir="))
                        {
                            string singerpath = line.Trim().Replace("VoiceDir=", string.Empty);
                            var singer = UtauSoundbank.GetSinger(singerpath, FileEncoding.DetectFileEncoding(file), DocManager.Inst.Singers);
                            if (singer == null) singer = new USinger() { Name = "", Path = singerpath };
                            project.Singers.Add(singer);
                            project.Tracks[0].Singer = singer;
                        }
                    }
                    else if (currentBlock == UstBlock.Note)
                    {
                        currentLines.Add(line);
                    }
                    else if (currentBlock == UstBlock.Trackend)
                    {
                        break;
                    }
                }
            }

            if (currentBlock != UstBlock.Trackend)
                DocManager.Inst.ExecuteCmd(new UserMessageNotification("Unexpected ust file end"));
            part.DurTick = currentTick;
            return project;
        }

        static UNote NoteFromUst(UNote note, List<string> lines, UstVersion version)
        {
            string pbs = "", pbw = "", pby = "", pbm = "";

            foreach (string line in lines)
            {
                if (line.StartsWith("Lyric="))
                {
                    note.Phonemes[0].Phoneme = note.Lyric = line.Trim().Replace("Lyric=", string.Empty);
                    if (note.Phonemes[0].Phoneme.StartsWith("?"))
                    {
                        note.Phonemes[0].Phoneme = note.Phonemes[0].Phoneme.Substring(1);
                        note.Phonemes[0].AutoRemapped = false;
                    }
                }
                if (line.StartsWith("Length=")) {
                    note.DurTick = int.Parse(line.Trim().Replace("Length=", string.Empty));
                }
                if (line.StartsWith("NoteNum=")) {
                    note.NoteNum = int.Parse(line.Trim().Replace("NoteNum=", string.Empty));
                }
                if (line.StartsWith("Velocity=")) {
                    note.Expressions["velocity"].Data = int.Parse(line.Trim().Replace("Velocity=", string.Empty));
                }
                if (line.StartsWith("Intensity=")) {
                    note.Expressions["volume"].Data = int.Parse(line.Trim().Replace("Intensity=", string.Empty));
                }
                if (line.StartsWith("PreUtterance="))
                {
                    if (line.Trim() == "PreUtterance=") {
                        note.Phonemes[0].AutoEnvelope = true;
                    }
                    else {
                        note.Phonemes[0].AutoEnvelope = false;
                        note.Phonemes[0].Preutter = double.Parse(line.Trim().Replace("PreUtterance=", string.Empty));
                    }
                }
                if (line.StartsWith("VoiceOverlap=")) {
                    if (line.Trim() == "VoiceOverlap=") {
                        note.Phonemes[0].Overlap = 0;
                    } else {
                        note.Phonemes[0].Overlap = double.Parse(line.Trim().Replace("VoiceOverlap=", string.Empty));
                    }
                } 
                if (line.StartsWith("Envelope="))
                {
                    //Envelope=0,37.3,35,0,100,100,0
                    var pts = line.Trim().Replace("Envelope=", string.Empty).Split(new[] { ',' });
                    note.Expressions["decay"].Data = (int)double.Parse(pts[5]);
                    //note.Expressions["accent"].Data = (int)double.Parse(pts[0]);
                    
                    #region Envelope Vals
                    //arraycnt = 0,1, 2,3,  4,  5,  6,7, 8, 9, 10
                    //env long = 0,5,20,0,100,100,100,%,15,10,100
                    //0 = {0,0}
                    //1 = { 5,100}
                    //3 = { 20,100}
                    //4 = { 15,100}
                    //2 = { 10,100}

                    //if (pts.Contains("%")) {
                    //    if (pts.Count() == 11) {
                    //        note.Phonemes[0].Envelope.Points[0].X = double.Parse(pts[0]) - note.Phonemes[0].Preutter;
                    //        note.Phonemes[0].Envelope.Points[0].Y = double.Parse(pts[3]);
                    //        note.Phonemes[0].Envelope.Points[1].X = double.Parse(pts[1]);
                    //        note.Phonemes[0].Envelope.Points[1].Y = double.Parse(pts[4]);
                    //        note.Phonemes[0].Envelope.Points[2].X = double.Parse(pts[9]);
                    //        note.Phonemes[0].Envelope.Points[2].Y = double.Parse(pts[10]);
                    //        note.Phonemes[0].Envelope.Points[3].X = double.Parse(pts[2]);
                    //        note.Phonemes[0].Envelope.Points[3].Y = double.Parse(pts[5]);
                    //        note.Phonemes[0].Envelope.Points[4].X = double.Parse(pts[8]);
                    //        note.Phonemes[0].Envelope.Points[4].Y = double.Parse(pts[6]);
                    //    } else if (pts.Count() == 9) {
                    //        //note.Expressions["decay"].Data = (int)double.Parse(pts[2]);
                    //        //note.Expressions["accent"].Data = (int)double.Parse(pts[1]);
                    //        note.Phonemes[0].Envelope.Points[0].X = double.Parse(pts[0]) - note.Phonemes[0].Preutter;
                    //        note.Phonemes[0].Envelope.Points[0].Y = double.Parse(pts[3]);
                    //        note.Phonemes[0].Envelope.Points[1].X = double.Parse(pts[1]);
                    //        note.Phonemes[0].Envelope.Points[1].Y = double.Parse(pts[4]);
                    //        note.Phonemes[0].Envelope.Points[2].X = 0;
                    //        note.Phonemes[0].Envelope.Points[2].Y = 100;
                    //        note.Phonemes[0].Envelope.Points[3].X = double.Parse(pts[2]);
                    //        note.Phonemes[0].Envelope.Points[3].Y = double.Parse(pts[5]);
                    //        note.Phonemes[0].Envelope.Points[4].X = double.Parse(pts[8]);
                    //        note.Phonemes[0].Envelope.Points[4].Y = double.Parse(pts[6]);
                    //    } else {
                    //        note.Phonemes[0].Envelope.Points[0].X = double.Parse(pts[0]) - note.Phonemes[0].Preutter;
                    //        note.Phonemes[0].Envelope.Points[0].Y = double.Parse(pts[3]);
                    //        note.Phonemes[0].Envelope.Points[1].X = double.Parse(pts[1]);
                    //        note.Phonemes[0].Envelope.Points[1].Y = double.Parse(pts[4]);
                    //        note.Phonemes[0].Envelope.Points[2].X = 0;
                    //        note.Phonemes[0].Envelope.Points[2].Y = 100;
                    //        note.Phonemes[0].Envelope.Points[3].X = double.Parse(pts[2]);
                    //        note.Phonemes[0].Envelope.Points[3].Y = double.Parse(pts[5]);
                    //        note.Phonemes[0].Envelope.Points[4].X = 0;
                    //        note.Phonemes[0].Envelope.Points[4].Y = double.Parse(pts[6]);
                    //    }
                    //}
                    //    //Order   =p1,p2,p3,v1,v2,v3,v4
                    //    //Envpoints=1x,2x,3x,1y,2y,3y,4y
                    //    else {
                    //    note.Phonemes[0].Envelope.Points[0].X = double.Parse(pts[0]) - note.Phonemes[0].Preutter;
                    //    note.Phonemes[0].Envelope.Points[0].Y = double.Parse(pts[3]);
                    //    note.Phonemes[0].Envelope.Points[1].X = double.Parse(pts[1]);
                    //    note.Phonemes[0].Envelope.Points[1].Y = double.Parse(pts[4]);
                    //    note.Phonemes[0].Envelope.Points[2].X = 0;
                    //    note.Phonemes[0].Envelope.Points[2].Y = 100;
                    //    note.Phonemes[0].Envelope.Points[3].X = double.Parse(pts[2]);
                    //    note.Phonemes[0].Envelope.Points[3].Y = double.Parse(pts[5]);
                    //    note.Phonemes[0].Envelope.Points[4].X = 0;
                    //    note.Phonemes[0].Envelope.Points[4].Y = double.Parse(pts[6]);
                    //}
                    #endregion
                }
                if (line.StartsWith("StartPoint=")) {
                    note.Expressions["accent"].Data = (int)double.Parse(line.Trim().Replace("StartPoint=", string.Empty));
                }

                if (line.StartsWith("VBR=")) VibratoFromUst(note.Vibrato, line.Trim().Replace("VBR=", string.Empty));
                if (line.StartsWith("PBS=")) pbs = line.Trim().Replace("PBS=", string.Empty);
                if (line.StartsWith("PBW=")) pbw = line.Trim().Replace("PBW=", string.Empty);
                if (line.StartsWith("PBY=")) pby = line.Trim().Replace("PBY=", string.Empty);
                if (line.StartsWith("PBM=")) pbm = line.Trim().Replace("PBM=", string.Empty);
            }

            if (pbs != string.Empty)
            {
                var pts = note.PitchBend.Data as List<PitchPoint>;
                pts.Clear();
                // PBS
                if (pbs.Contains(';'))
                {
                    pts.Add(new PitchPoint(double.Parse(pbs.Split(new[] { ';' })[0]), double.Parse(pbs.Split(new[] { ';' })[1])));
                        note.PitchBend.SnapFirst = false;
                }
                else
                {
                    pts.Add(new PitchPoint(double.Parse(pbs), 0));
                    note.PitchBend.SnapFirst = true;
                }
                double x = pts.First().X;
                if (pbw != string.Empty)
                {
                    string[] w = pbw.Split(new[] { ',' });
                    string[] y = null;
                    if (w.Count() > 1) y = pby.Split(new[] { ',' });
                    for (int i = 0; i < w.Count() - 1; i++)
                    {
                        x += string.IsNullOrEmpty(w[i]) ? 0 : float.Parse(w[i]);
                        if (y.Count() > 1) {
                            if (y.Count() < w.Count()) {
                                if(i >= y.Count()) {
                                    pts.Add(new PitchPoint(x, string.IsNullOrEmpty(y[y.Count()-1]) ? 0 : double.Parse(y[y.Count() - 1])));
                                } else {
                                    pts.Add(new PitchPoint(x, string.IsNullOrEmpty(y[i]) ? 0 : double.Parse(y[i])));
                                }
                            } else {
                                pts.Add(new PitchPoint(x, string.IsNullOrEmpty(y[i]) ? 0 : double.Parse(y[i])));
                            }
                        } else {
                            pts.Add(new PitchPoint(x, string.IsNullOrEmpty(y[0]) ? 0 : double.Parse(y[0])));
                        }
                    }
                    pts.Add(new PitchPoint(x + double.Parse(w[w.Count() - 1]), 0));
                }
                if (pbm != string.Empty)
                {
                    string[] m = pbw.Split(new[] { ',' });
                    for (int i = 0; i < m.Count() - 1; i++)
                    {
                        pts[i].Shape = m[i] == "r" ? PitchPointShape.o :
                                       m[i] == "s" ? PitchPointShape.l :
                                       m[i] == "j" ? PitchPointShape.i : PitchPointShape.io;
                    }
                }
            }
            Log.Warning(note.ToString());
            return note;
        }

        static void VibratoFromUst(VibratoExpression vibrato, string ust)
        {
            var args = ust.Split(new[] { ',' }).Select(double.Parse).ToList();
            if (args.Count() >= 7)
            {
                vibrato.Length = args[0];
                vibrato.Period = args[1];
                vibrato.Depth = args[2];
                vibrato.In = args[3];
                vibrato.Out = args[4];
                vibrato.Shift = args[5];
                vibrato.Drift = args[6];
            }
        }

        static String VibratoToUst(VibratoExpression vibrato)
        {
            List<double> args = new List<double>()
            {
                vibrato.Length,
                vibrato.Period,
                vibrato.Depth,
                vibrato.In,
                vibrato.Out,
                vibrato.Shift,
                vibrato.Drift
            };
            return string.Join(",", args.ToArray());
        }
    }
}
