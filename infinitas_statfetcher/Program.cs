﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Globalization;

namespace infinitas_statfetcher
{
    class Program
    {
        /* Import external methods */
        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        readonly static HttpClient client = new HttpClient();
        static void Main()
        {
            Process process = null;
            Config.Parse("config.ini");

            DirectoryInfo sessionDir = new DirectoryInfo("sessions");
            if (!sessionDir.Exists)
            {
                sessionDir.Create();
            }
            DateTime now = DateTime.Now;

            CultureInfo culture = CultureInfo.CreateSpecificCulture("en-us");
            DateTimeFormatInfo dtformat = culture.DateTimeFormat;
            dtformat.TimeSeparator = "_";
            var sessionFile = new FileInfo(Path.Combine(sessionDir.FullName, $"Session_{now:yyyy_MM_dd_hh_mm_ss}.tsv"));

            Console.WriteLine("Trying to hook to INFINITAS...");
            do
            {
                var processes = Process.GetProcessesByName("bm2dx");
                if (processes.Any())
                {
                    process = processes[0];
                }

                Thread.Sleep(2000);
            } while (process == null);

            Console.Clear();
            Console.WriteLine("Hooked to process, waiting until song list is loaded...");


            Utils.LoadEncodingFixes();

            IntPtr processHandle = OpenProcess(0x0410, false, process.Id); /* Open process for memory read */
            Utils.handle = processHandle;

            var songDb = new Dictionary<string, SongInfo>();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Offsets.LoadOffsets("offsets.txt");


            bool songlistFetched = false;
            while (!songlistFetched)
            {
                while (!Utils.SongListAvailable()) { Thread.Sleep(5000); } /* Don't fetch song list until it seems loaded */
                songDb = Utils.FetchSongDataBase();
                if (songDb["80003"].totalNotes[3] < 10) /* If Clione (Ryu* Remix) SPH has less than 10 notes, the songlist probably wasn't completely populated when we fetched it. That memory space generally tends to hold 0, 2 or 3, depending on which 'difficulty'-doubleword you're reading */
                {
                    Console.WriteLine("Notecount data seems bad, retrying fetching in case list wasn't fully populated.");
                    Thread.Sleep(5000);
                }
                else
                {
                    songlistFetched = true;
                }
            }
            File.Create(sessionFile.FullName).Close();
            WriteLine(sessionFile, Config.GetTsvHeader());

            //upload_songlist(songDb);
            /* Primarily for debugging and checking for encoding issues */
            if (Config.Output_songlist)
            {
                List<string> p = new List<string>() { "id,title,title2,artist,genre" };
                foreach (var v in songDb)
                {
                    p.Add($"{v.Key},{v.Value.title},{v.Value.title_english},{v.Value.artist},{v.Value.genre}");
                }
                File.WriteAllLines("songs.csv", p.ToArray());
            }
            GameState state = GameState.finished;

            while (!process.HasExited)
            {
                var newstate = Utils.FetchGameState();
                if (newstate != state)
                {
                    Console.Clear();
                    Console.WriteLine($"STATUS:{(newstate == GameState.finished ? " NOT" : "")} PLAYING");
                    if (newstate == GameState.finished)
                    {
                        var latestData = new PlayData();
                        latestData.Fetch(songDb);
                        if (Config.Save_remote)
                        {
                            SendPlayData(latestData);
                        }
                        if (Config.Save_local)
                        {
                            WriteLine(sessionFile, latestData.GetTsvEntry());
                        }
                        Print_PlayData(latestData);
                    }
                }
                state = newstate;

                Thread.Sleep(2000);
            }

            Cleanup();
        }

        static void Print_PlayData(PlayData latestData)
        {
            Console.WriteLine("\nLATEST CLEAR:");

            var header = Config.GetTsvHeader();
            var entry = latestData.GetTsvEntry();

            var h = header.Split('\t');
            var e = entry.Split('\t');
            for(int i = 0; i < h.Length; i++)
            {
                Console.WriteLine("{0,10}: {1,15}", h[i], e[i]);
            }
        }
        static void WriteLine(FileInfo file, string str)
        {
            File.AppendAllLines(file.FullName, new string[] { str });
        }
        static void DumpStackMemory()
        {

        }

        static async void SendPlayData(PlayData latestData)
        {

            var content = new FormUrlEncodedContent(latestData.ToPostForm());

            try
            {
                var response = await client.PostAsync(Config.Server + "/api/songplayed", content);
                //var response = await client.PostAsync("http://127.0.0.1:5000/api/songplayed", content);

                var responseString = await response.Content.ReadAsStringAsync();
                Console.WriteLine(responseString);
            }
            catch
            {
                Console.WriteLine("Uploading failed");
            }


        }

        private static void Cleanup()
        {
            /* Save files and whatever that should carry over sessions */
        }

    }

    #region Custom objects
    enum GameState { started = 0, finished };
    enum OffSetCategories { judgeInfo = 0, noteInfo, songInfo }
    public enum PlayType { P1 = 0, P2, DP }
    public struct SongInfo
    {
        public string ID;
        public int[] totalNotes; /* SPB, SPN, SPH, SPA, SPL, DPB, DPN, DPH, DPA, DPL */
        public int[] level; /* SPB, SPN, SPH, SPA, SPL, DPB, DPN, DPH, DPA, DPL */
        public string title;
        public string title_english;
        public string artist;
        public string genre;
        public string bpm;
    }
    #endregion
}
