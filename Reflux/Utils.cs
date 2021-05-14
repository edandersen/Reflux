﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace infinitas_statfetcher
{
    public enum Difficulty { 
        SPB = 0,
        SPN,
        SPH,
        SPA,
        SPL,
        DPB,
        DPN,
        DPH,
        DPA,
        DPL
    }
    public enum Lamp
    {
        NP = 0,
        F,
        AC,
        EC,
        NC,
        HC,
        EX,
        FC,
        PFC
    }
    public enum Grade
    {
        NP = 0,
        F,
        E,
        D,
        C,
        B,
        A,
        AA,
        AAA,
    }

    /// <summary>
    /// All metadata for a song and its charts
    /// </summary>
    public struct SongInfo
    {
        public string ID;
        public int[] totalNotes; /* SPB, SPN, SPH, SPA, SPL, DPB, DPN, DPH, DPA, DPL */
        public int[] level; /* SPB, SPN, SPH, SPA, SPL, DPB, DPN, DPH, DPA, DPL */
        public string title;
        public string title_english;
        public string artist;
        public unlockType type;
        public string genre;
        public string bpm;
    }
    /// <summary>
    /// Information saved to the local tracker file
    /// </summary>
    public struct TrackerInfo
    {
        public Grade grade;
        public Lamp lamp;
        public int ex_score;
        public int misscount;
    }
    /// <summary>
    /// Generic chart object for dictionary key lookup
    /// </summary>
    public struct Chart
    {
        public string songID;
        public Difficulty difficulty;
    }
    /// <summary>
    /// The three different kind of song unlock types, Bits are anything that is visible while locked, and Sub is anything that is not visible while locked
    /// </summary>
    public enum unlockType { Base = 1, Bits, Sub }; // Assume subscription songs unless specifically addressed in customtypes.txt
    /// <summary>
    /// Structure of the unlock data array in memory
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UnlockData
    {
        public Int32 songID;
        public unlockType type;
        public Int32 unlocks;
    };
    class Utils
    {
        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(int hProcess,
            Int64 lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);

        public static IntPtr handle;
        /// <summary>
        /// DB for keeping track of unlocks and potential changes
        /// </summary>
        public static Dictionary<string, UnlockData> unlockDb = new Dictionary<string, UnlockData>();
        /// <summary>
        /// DB for easily looking up songs and their chart information
        /// </summary>
        public static Dictionary<string, SongInfo> songDb = new Dictionary<string, SongInfo>();
        /// <summary>
        /// DB for keeping track of PBs on different charts
        /// </summary>
        public static Dictionary<Chart, TrackerInfo> trackerDb = new Dictionary<Chart, TrackerInfo>();
        /// <summary>
        /// DB of known encoding issues or inconsistencies to how they're generally known
        /// </summary>
        readonly static Dictionary<string, string> knownEncodingIssues = new Dictionary<string, string>();
        /// <summary>
        /// DB of custom types of unlocks, to separate DJP unlocks from bit unlocks and song pack unlocks from subscription songs
        /// </summary>
        readonly static Dictionary<string, string> customTypes = new Dictionary<string, string>();

        public static Grade ScoreToGrade(string songID, Difficulty difficulty, int exscore)
        {
            var maxEx = Utils.songDb[songID].totalNotes[(int)difficulty] * 2;
            var exPart = (double)maxEx / 9;


            if (exscore > exPart * 8)
            {
                return Grade.AAA;
            }
            else if (exscore > exPart * 7)
            {
                return Grade.AA;
            }
            else if (exscore > exPart * 6)
            {
                return Grade.A;
            }
            else if (exscore > exPart * 5)
            {
                return Grade.B;
            }
            else if (exscore > exPart * 4)
            {
                return Grade.C;
            }
            else if (exscore > exPart * 3)
            {
                return Grade.D;
            }
            else if (exscore > exPart * 2)
            {
                return Grade.E;
            }
            else if(exscore == 0)
            {
                return Grade.NP;
            }
            return Grade.F;


        }
        /// <summary>
        /// Populate DB for encoding issues, tab separated since commas can appear in title
        /// </summary>
        public static void LoadEncodingFixes()
        {
            try
            {
                foreach (var line in File.ReadAllLines("encodingfixes.txt"))
                {
                    if (!line.Contains('\t')) { continue; } /* Skip version string */
                    var pair = line.Split('\t');
                    knownEncodingIssues.Add(pair[0], pair[1].Trim());
                }
            } catch (Exception e)
            {
                Except(e);
            }
        }
        /// <summary>
        /// Populate DB for custom unlock types, comma separated
        /// </summary>
        public static void LoadCustomTypes()
        {
            try {
                foreach (var line in File.ReadAllLines("customtypes.txt"))
                {
                    if (!line.Contains(',')) { continue; } /* Skip version string */
                    var pair = line.Split(',');
                    customTypes.Add(pair[0], pair[1].Trim());
                }
            }
            catch (Exception e)
            {
                Except(e);
            }
        }
        /// <summary>
        /// Figure out if INFINITAS is currently playing a song, showing the results or hanging out in the song select
        /// </summary>
        /// <param name="currentState"></param>
        /// <returns></returns>
        public static GameState FetchGameState(GameState currentState)
        {
            short word = 4;

            var marker = ReadInt32(Offsets.JudgeData, word * 24);
            if (marker != 0)
            {
                return GameState.playing;
            }

            /* Cannot go from song select to result screen anyway */
            if(currentState == GameState.songSelect) { return currentState; }
            marker = ReadInt32(Offsets.PlaySettings - word * 5, 0);
            if (marker == 1)
            {
                return GameState.songSelect;
            }
            return GameState.resultScreen;
        }
        /// <summary>
        /// Fetch and format the current chart for saving to currentsong.txt
        /// </summary>
        /// <param name="includeDiff"></param>
        /// <returns></returns>
        public static string CurrentChart(bool includeDiff = false)
        {
            var values = FetchCurrentChart();
            return $"{songDb[values.songID].title_english}{(includeDiff ? " " + values.difficulty.ToString() : "")}";
        }
        /// <summary>
        /// Populate database for song metadata
        /// </summary>
        public static void FetchSongDataBase()
        {
            Dictionary<string, SongInfo> result = new Dictionary<string, SongInfo>();
            Debug("Fetching available songs");
            var current_position = 0;
            while (true)
            {

                var songInfo = FetchSongInfo(Offsets.SongList + current_position);

                if (songInfo.title == null)
                {
                    Debug("Songs fetched.");
                    break;
                }

                if (knownEncodingIssues.ContainsKey(songInfo.title))
                {
                    var old = songInfo.title;
                    songInfo.title = knownEncodingIssues[songInfo.title];
                    Debug($"Fixed encoding issue \"{old}\" with \"{songInfo.title}\"");
                }
                if (knownEncodingIssues.ContainsKey(songInfo.artist))
                {
                    var old = songInfo.artist;
                    songInfo.artist = knownEncodingIssues[songInfo.artist];
                    Debug($"Fixed encoding issue \"{old}\" with \"{songInfo.artist}\"");
                }
                if (!result.ContainsKey(songInfo.ID))
                {
                    result.Add(songInfo.ID, songInfo);
                }

                current_position += 0x3F0;

            }
            songDb = result;
        }
        /// <summary>
        /// Util function to get an 32-bit integer from any section in a byte array
        /// </summary>
        /// <param name="input">Byte array to get value from</param>
        /// <param name="skip">Amount of bytes to skip before parsing</param>
        /// <param name="take">Amount of bytes to use for parsing</param>
        /// <returns></returns>
        public static Int32 BytesToInt32(byte[] input, int skip, int take = 4)
        {
            if (skip == 0)
            {
                return BitConverter.ToInt32(input.Take(take).ToArray());
            }
            return BitConverter.ToInt32(input.Skip(skip).Take(take).ToArray());
        }
        /// <summary>
        /// Util function to get an 64-bit integer from any section in a byte array
        /// </summary>
        /// <param name="input">Byte array to get value from</param>
        /// <param name="skip">Amount of bytes to skip before parsing</param>
        /// <param name="take">Amount of bytes to use for parsing</param>
        /// <returns></returns>
        public static Int64 BytesToInt64(byte[] input, int skip, int take = 8)
        {
            if (skip == 0)
            {
                return BitConverter.ToInt64(input.Take(take).ToArray());
            }
            return BitConverter.ToInt64(input.Skip(skip).Take(take).ToArray());
        }
        [Conditional("DEBUG")]
        public static void Debug(string msg)
        {
            Console.WriteLine(msg);
        }
        /// <summary>
        /// Print exception message to log for easier viewing
        /// </summary>
        /// <param name="e"></param>
        /// <param name="context"></param>
        public static void Except(Exception e, string context="")
        {
            var stream = File.AppendText("crashlog.txt");
            stream.WriteLine($"{(context == "" ? "Unhandled exception" : context )}: {e.Message}");
        }
        public static void Log(string message)
        {
            var stream = File.AppendText("log.txt");
            stream.WriteLine(message);
        }

        #region Memory reading functions
        /// <summary>
        /// Find the song and difficulty that is currently being played
        /// </summary>
        /// <returns></returns>
        public static Chart FetchCurrentChart()
        {
            byte[] buffer = new byte[32];
            int nRead = 0;
            ReadProcessMemory((int)handle, Offsets.CurrentSong, buffer, buffer.Length, ref nRead);
            int songid = BytesToInt32(buffer, 0);
            int diff = BytesToInt32(buffer, 4);
            return new Chart() { songID = songid.ToString("D5"), difficulty = (Difficulty)diff };
        }
        /// <summary>
        /// Figure out if all necessary data for populating different DBs are available
        /// </summary>
        /// <returns></returns>
        public static bool DataLoaded()
        {
            byte[] buffer = new byte[64];
            int nRead = 0;
            ReadProcessMemory((int)handle, Offsets.SongList, buffer, buffer.Length, ref nRead);
            var title = Encoding.GetEncoding("Shift-JIS").GetString(buffer.Where(x => x != 0).ToArray());
            var titleNoFilter = Encoding.GetEncoding("Shift-JIS").GetString(buffer);
            buffer = new byte[4];
            ReadProcessMemory((int)handle, Offsets.UnlockData, buffer, buffer.Length, ref nRead);
            var id = Utils.BytesToInt32(buffer, 0);
            Debug($"Read string: \"{title}\" in start of song list, expecting \"5.1.1.\"");
            Debug($"Read number: {id} in start of unlock list, expecting 1000");
            return title.Contains("5.1.1.") && id == 1000;
        }
        /// <summary>
        /// Function to read any position in memory and convert to Int32
        /// </summary>
        /// <param name="position">Base offset in memory</param>
        /// <param name="offset">Potential extra offset for readability instead of just adding to <paramref name="position"/></param>
        /// <param name="size">Amount of bytes to read and convert</param>
        /// <returns></returns>
        public static Int32 ReadInt32(long position, int offset, int size = 4)
        {
            int bytesRead = 0;

            byte[] buffer = new byte[size];

            ReadProcessMemory((int) handle, position+offset, buffer, buffer.Length, ref bytesRead);
            return Utils.BytesToInt32(buffer.Take(size).ToArray(), 0);
        }
        /// <summary>
        /// Function to read any position in memory and convert to Int64
        /// </summary>
        /// <param name="position">Base offset in memory</param>
        /// <param name="offset">Potential extra offset for readability instead of just adding to <paramref name="position"/></param>
        /// <param name="size">Amount of bytes to read and convert</param>
        /// <returns></returns>
        public static Int64 ReadInt64(long position, int offset, int size = 8)
        {
            int bytesRead = 0;

            byte[] buffer = new byte[size];

            ReadProcessMemory((int) handle, position+offset, buffer, buffer.Length, ref bytesRead);
            return Utils.BytesToInt64(buffer.Take(size).ToArray(), 0);
        }
        /// <summary>
        /// Fetch metadata for one song
        /// </summary>
        /// <param name="position">Start position of song metadata</param>
        /// <returns>SongInfo object containing all metadata</returns>
        private static SongInfo FetchSongInfo(long position)
        {
            int bytesRead = 0;
            short slab = 64;
            short word = 4; /* Int32 */

            byte[] buffer = new byte[1008];

            ReadProcessMemory((int)handle, position, buffer, buffer.Length, ref bytesRead);

            var title1 = Encoding.GetEncoding("Shift-JIS").GetString(buffer.Take(slab).Where(x => x != 0).ToArray());

            if (Utils.BytesToInt32(buffer.Take(slab).ToArray(), 0) == 0)
            {
                return new SongInfo();
            }

            var title2 = Encoding.GetEncoding("Shift-JIS").GetString(buffer.Skip(slab).Take(slab).Where(x => x != 0).ToArray());
            var genre = Encoding.GetEncoding("Shift-JIS").GetString(buffer.Skip(slab * 2).Take(slab).Where(x => x != 0).ToArray());
            var artist = Encoding.GetEncoding("Shift-JIS").GetString(buffer.Skip(slab * 3).Take(slab).Where(x => x != 0).ToArray());

            var diff_section = buffer.Skip(slab * 4 + slab / 2).Take(10).ToArray();
            var diff_levels = new int[] { 
                Convert.ToInt32(diff_section[0]),
                Convert.ToInt32(diff_section[1]),
                Convert.ToInt32(diff_section[2]),
                Convert.ToInt32(diff_section[3]),
                Convert.ToInt32(diff_section[4]),
                Convert.ToInt32(diff_section[5]),
                Convert.ToInt32(diff_section[6]),
                Convert.ToInt32(diff_section[7]),
                Convert.ToInt32(diff_section[8]),
                Convert.ToInt32(diff_section[9]) };

            var bpms = buffer.Skip(slab * 5).Take(8).ToArray();
            var noteCounts_bytes = buffer.Skip(slab * 6 + 48).Take(slab).ToArray();

            var bpmMax = Utils.BytesToInt32(bpms, 0);
            var bpmMin = Utils.BytesToInt32(bpms, word);

            string bpm = "NA";
            if (bpmMin != 0)
            {
                bpm = $"{bpmMin:000}~{bpmMax:000}";
            }
            else
            {
                bpm = bpmMax.ToString("000");
            }

            var noteCounts = new int[] { 
                Utils.BytesToInt32(noteCounts_bytes, 0),
                Utils.BytesToInt32(noteCounts_bytes, word),
                Utils.BytesToInt32(noteCounts_bytes, word * 2),
                Utils.BytesToInt32(noteCounts_bytes, word * 3),
                Utils.BytesToInt32(noteCounts_bytes, word * 4),
                Utils.BytesToInt32(noteCounts_bytes, word * 5),
                Utils.BytesToInt32(noteCounts_bytes, word * 6),
                Utils.BytesToInt32(noteCounts_bytes, word * 7),
                Utils.BytesToInt32(noteCounts_bytes, word * 8),
                Utils.BytesToInt32(noteCounts_bytes, word * 9) 
            };


            var idarray = buffer.Skip(256 + 368).Take(4).ToArray();

            var ID = BitConverter.ToInt32(idarray, 0).ToString("D5");

            var song = new SongInfo
            {
                ID = ID,
                title = title1,
                title_english = title2,
                genre = genre,
                artist = artist,
                bpm = bpm,
                totalNotes = noteCounts,
                level = diff_levels
            };

            return song;

        }
        #endregion

        #region Unlock database related
        /// <summary>
        /// Update and detect changes to song unlock states
        /// </summary>
        /// <returns>Changes between the two unlock statuses, if any. Empty dictionary otherwise</returns>
        public static Dictionary<string, UnlockData> UpdateUnlockStates()
        {
            var oldUnlocks = unlockDb;
            GetUnlockStates();
            var changes = new Dictionary<string, UnlockData>();
            foreach(var key in unlockDb.Keys)
            {
                if (!oldUnlocks.ContainsKey(key)) {
                    Log($"Key {key} was not present in past unlocks array"); 
                    continue;
                }
                if (unlockDb[key].unlocks != oldUnlocks[key].unlocks)
                {
                    UnlockData value = unlockDb[key];
                    changes.Add(key, value);
                    oldUnlocks[key] = unlockDb[key];
                }
            }
            return changes;
        }
        /// <summary>
        /// Read and populate a dictionary for the unlock information of all songs
        /// </summary>
        /// <returns></returns>
        public static Dictionary<string, UnlockData> GetUnlockStates()
        {
            int songAmount = songDb.Count;
            int structSize = Marshal.SizeOf(typeof(UnlockData));
            byte[] buf = new byte[structSize * songAmount];
            int nRead = 0;

            /* Read information for all songs at once and cast to struct array after */
            ReadProcessMemory((int)handle, Offsets.UnlockData, buf, buf.Length, ref nRead);

            unlockDb = new Dictionary<string, UnlockData>();
            var extra = ParseUnlockBuffer(buf);

            /* Handle offset issues caused by unlock data having information on songs not present in song db */
            int moreExtra = 0;
            while(extra > 0)
            {
                buf = new byte[structSize * extra];
                ReadProcessMemory((int)handle, Offsets.UnlockData + structSize * (songAmount + moreExtra), buf, buf.Length, ref nRead);
                moreExtra = ParseUnlockBuffer(buf);
                extra = moreExtra;
            }

            return unlockDb;
        }

        /// <summary>
        /// Convert a byte array to an <see cref="UnlockData"/> object
        /// </summary>
        /// <param name="buf">Byte array</param>
        /// <returns>An <see cref="UnlockData"/> object representation of the input</returns>
        static int ParseUnlockBuffer(byte[] buf)
        {
            int position = 0;
            int extra = 0;
            int structSize = Marshal.SizeOf(typeof(UnlockData));
            while(position < buf.Length)
            {
                var sData = buf.Skip(position).Take(structSize).ToArray();
                UnlockData data = new UnlockData { 
                    songID = BytesToInt32(sData, 0),
                    type = (unlockType)BytesToInt32(sData, 4),
                    unlocks = BytesToInt32(sData, 8) };
                string id = data.songID.ToString("D5");
                if(id == "00000") /* Take into account where songDb is populated with unreleased songs */
                {
                    break;
                }
                unlockDb.Add(id, data);
                try
                {
                    var song = songDb[id];
                    song.type = data.type;
                    songDb[id] = song;
                } catch
                {
                    Debug($"Song {id} not present in song database");
                    extra++;
                }

                position += structSize;
            }
            return extra;

        }
        /// <summary>
        /// Get the unlock state for a specific chart of a song
        /// </summary>
        /// <param name="songid">SongID of interest</param>
        /// <param name="diff">Chart difficulty</param>
        /// <returns>True if unlocked, false if locked</returns>
        public static bool GetUnlockStateForDifficulty(string songid, Difficulty diff)
        {
            var unlockBits = unlockDb[songid].unlocks;
            int bit = 1 << (int)diff;
            return (bit & unlockBits) > 0;
        }
        #endregion

        #region Tracker related
        /// <summary>
        /// Save tracker tsv and unlockdb, as they're somewhat interlinked due to the unlock information in both
        /// </summary>
        /// <param name="filename"></param>
        public static void SaveTrackerData(string filename)
        {

            try
            {
                StringBuilder sb = new StringBuilder();
                StringBuilder db = new StringBuilder();
                sb.AppendLine("title\tType\tLabel\tCost Normal\tCost Hyper\tCost Another\tSPN\tSPN Rating\tSPN Lamp\tSPN Letter\tSPN EX Score\tSPN Miss Count\tSPH\tSPH Rating\tSPH Lamp\tSPH Letter\tSPH EX Score\tSPH Miss Count\tSPA\tSPA Rating\tSPA Lamp\tSPA Letter\tSPA EX Score\tSPA Miss Count\tDPN\tDPN Rating\tDPN Lamp\tDPN Letter\tDPN EX Score\tDPN Miss Count\tDPH\tDPH Rating\tDPH Lamp\tDPH Letter\tDPH EX Score\tDPH Miss Count\tDPA\tDPA Rating\tDPA Lamp\tDPA Letter\tDPA EX Score\tDPA Miss Count");
                foreach (var entry in Utils.GetTrackerEntries())
                {
                    sb.AppendLine(entry);
                }
                File.WriteAllText(filename, sb.ToString());
                if (Config.Save_remote)
                {
                    foreach (var song in unlockDb)
                    {
                        db.AppendLine($"{song.Key},{(int)song.Value.type},{song.Value.unlocks}");
                    }
                    File.WriteAllText("unlockdb", db.ToString());
                }
            } catch (Exception e)
            {
                Except(e);
            }
        }
        /// <summary>
        /// Get each song entry for the tracker TSV
        /// </summary>
        /// <returns>Lazily evaluated list of entries</returns>
        static IEnumerable<string> GetTrackerEntries()
        {
            foreach(var songid in trackerDb.Keys.Select(x => x.songID).Distinct())
            {
                var song = unlockDb[songid];
                string identifier = customTypes.ContainsKey(songid) ? customTypes[songid] : song.type.ToString();

                StringBuilder sb = new StringBuilder($"{songDb[songid].title}\t{song.type}\t{identifier}\t");

                StringBuilder bitCostData = new StringBuilder();
                StringBuilder chartData = new StringBuilder();
                for(int i = 0; i < 10; i++)
                {
                    /* Skip beginner and leggendaria */
                    if(i == (int)Difficulty.SPB || i == (int)Difficulty.SPL || i == (int)Difficulty.DPB || i == (int)Difficulty.DPL) { continue; }
                    Chart chart = new Chart() { songID = songid, difficulty = (Difficulty)i };
                    if (!trackerDb.ContainsKey(chart))
                    {
                        if (i < (int)Difficulty.DPB)
                        {
                            bitCostData.Append($"\t");
                        }
                        chartData.Append("\t\t\t\t");
                    }
                    else
                    {
                        bool unlockState = GetUnlockStateForDifficulty(songid, chart.difficulty);
                        if (i < (int)Difficulty.DPB)
                        {
                            var levels = songDb[songid].level;
                            int cost = (song.type == unlockType.Bits && !customTypes.ContainsKey(songid) ? 500 * (levels[(int)chart.difficulty] + levels[(int)chart.difficulty + (int)Difficulty.DPB]) : 0);
                            bitCostData.Append($"{cost}\t");
                        }
                        chartData.Append($"{(unlockState ? "TRUE" : "FALSE")}\t");
                        chartData.Append($"{songDb[songid].level[(int)chart.difficulty]}\t");
                        chartData.Append($"{trackerDb[chart].lamp}\t");
                        chartData.Append($"{trackerDb[chart].grade}\t");
                        chartData.Append($"{trackerDb[chart].ex_score}\t");
                        chartData.Append($"{trackerDb[chart].misscount}\t");
                    }
                }
                sb.Append(bitCostData);
                sb.Append(chartData);

                yield return sb.ToString();
            }
        }
        /// <summary>
        /// If saving to remote, load tracker.db if exist, otherwise create, populate potential new songs with data from score data hash map
        /// When not saving to remote, just generate the tracker info from INFINITAS internal hash map
        /// </summary>
        public static void LoadTracker()
        {
            /* Initialize if tracker file don't exist */
            if (Config.Save_remote && File.Exists("tracker.db"))
            {
                try
                {
                    foreach (var line in File.ReadAllLines("tracker.db"))
                    {
                        var segments = line.Split(',');
                        trackerDb.Add(new Chart() { songID = segments[0], difficulty = (Difficulty)Enum.Parse(typeof(Difficulty), segments[1]) }, new TrackerInfo() { grade = (Grade)Enum.Parse(typeof(Grade), segments[2]), lamp = (Lamp)Enum.Parse(typeof(Lamp), segments[3]), ex_score = int.Parse(segments[4]), misscount = int.Parse(segments[5]) });
                    }
                }
                catch (Exception e)
                {
                    Except(e);
                }
            }
            /* Add any potentially new songs */
            foreach (var song in songDb)
            {
                for (int i = 0; i < song.Value.level.Length; i++)
                {
                    /* Skip charts with no difficulty rating */
                    if (song.Value.level[i] == 0) { continue; }

                    var c = new Chart() { songID = song.Key, difficulty = (Difficulty)i };

                    if (!trackerDb.ContainsKey(c)) {
                        trackerDb.Add(c, new TrackerInfo() { 
                            lamp = ScoreMap.Scores[song.Key].lamp[i], 
                            grade = ScoreToGrade(song.Key, (Difficulty)i, ScoreMap.Scores[song.Key].score[i]),
                            ex_score = ScoreMap.Scores[song.Key].score[i],
                            misscount = ScoreMap.Scores[song.Key].misscount[i] 
                        });
                    }
                }
            }
            SaveTracker();
        }
        /// <summary>
        /// Save tracker information to tracker.db for memory between executions
        /// </summary>
        public static void SaveTracker()
        {
            if (Config.Save_remote)
            {
                try
                {
                    List<string> entries = new List<string>();
                    foreach (var entry in trackerDb)
                    {
                        entries.Add($"{entry.Key.songID},{entry.Key.difficulty},{entry.Value.grade},{entry.Value.lamp},{entry.Value.ex_score},{entry.Value.misscount}");
                    }
                    Debug("Saving tracker.db");
                    File.WriteAllLines("tracker.db", entries.ToArray());
                }
                catch (Exception e)
                {
                    Except(e);
                }
            }
        }
        #endregion
    }
}