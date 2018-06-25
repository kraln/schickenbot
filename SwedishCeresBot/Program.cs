using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Extensions;
using TwitchLib.Client.Models;

namespace SwedishCeresBot
{
    class Program
    {
        public static string user = "shickenbot";
        public static string oauth = "";
        public static string channel = "";

        public const string database_fn = "History.sqlite";
        public const string config_fn = "bot.txt";

        public static bool round_started = false;
        private static long round_id = 0;
        private static long round_started_time = 0;
        private static long round_awarded = 0; // how many awards this round
        private static bool round_more_guesses = true;
        public static TwitchClient cl; 

        public static System.Data.SQLite.SQLiteConnection con;
        static void Main(string[] args)
        {
            if (File.Exists(config_fn))
            {
                StreamReader file = new StreamReader(config_fn);
                user = file.ReadLine(); // instructions
                user = file.ReadLine();
                oauth = file.ReadLine();
                channel = file.ReadLine();
            }

            Thread mainThread = new Thread(Program.MainThread);
            System.Console.WriteLine("ShickenBot starting up!");

            /* prep database */
            bool first = false;
            if (!File.Exists(database_fn))
            {
                System.Data.SQLite.SQLiteConnection.CreateFile(database_fn);
                first = true;
                System.Console.WriteLine("Initializing empty statistics database!");
            }

            con = new System.Data.SQLite.SQLiteConnection("data source=" + database_fn);

            if (first)
            {
                /* create the table */
                using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
                {
                    con.Open();
                    com.CommandText = @"CREATE TABLE IF NOT EXISTS [channels] (
                                            [ID] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                                            [channel_name] TEXT UNIQUE NOT NULL
                                        )";
                    com.ExecuteNonQuery();
                    com.CommandText = @"CREATE TABLE IF NOT EXISTS [rounds] (
                                            [ID] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                                            [chan_id] INTEGER NOT NULL,
                                            [began] TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                            [guess] TEXT DEFAULT NULL,
                                            FOREIGN KEY (chan_id) REFERENCES channels(ID)
                                        )";
                    com.ExecuteNonQuery();
                    com.CommandText = @"CREATE TABLE IF NOT EXISTS [players] (
                                            [ID] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                                            [chan_id] INTEGER NOT NULL,
                                            [nickname] TEXT NOT NULL,
                                            [points] INTEGER DEFAULT 0,
                                            FOREIGN KEY (chan_id) REFERENCES channels(ID),
                                            UNIQUE (chan_id, nickname) ON CONFLICT REPLACE
                                        )";
                    com.ExecuteNonQuery();
                    com.CommandText = @"CREATE TABLE IF NOT EXISTS [guesses] (
                                            [ID] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                                            [round_id] INTEGER NOT NULL,
                                            [user_id] TEXT NOT NULL,
                                            [chan_id] INTEGER NOT NULL,
                                            [t] TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                            [guess] TEXT NOT NULL,
                                            FOREIGN KEY (user_id) REFERENCES players(ID),
                                            FOREIGN KEY (round_id) REFERENCES rounds(ID),
                                            FOREIGN KEY (chan_id) REFERENCES channels(ID)
                                        )";
                    com.ExecuteNonQuery();
                    con.Close();
                }
            }
            else
            {
                long[] stat = stats();
                System.Console.WriteLine("Loaded statistics database. " + stat[0] + " viewers, " + stat[1] + " rounds, " + stat[2] + " guesses tracked across " + stat[3] + " channels.");
            }

            /* launch chat */
            mainThread.Start();
            while (Console.Read() != 13) ;
        }

        private static long[] stats()
        {
                long playerCount = 0;
                long roundCount = 0;
                long guessCount = 0;
                long channelCount = 0;
                using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
                {
                    con.Open();
                    com.CommandText = "Select Count(*) from players";
                    playerCount = (long)com.ExecuteScalar();
                    com.CommandText = "Select Count(*) from rounds";
                    roundCount = (long)com.ExecuteScalar();
                    com.CommandText = "Select Count(*) from guesses";
                    guessCount = (long)com.ExecuteScalar();
                    com.CommandText = "Select Count(*) from channels";
                    channelCount = (long)com.ExecuteScalar();
                    con.Close();
                }

            return new long[] { playerCount, roundCount, guessCount, channelCount };
        }

        private static void MainThread()
        {
            ConnectionCredentials credentials = new ConnectionCredentials(user, oauth);
            cl = new TwitchClient();
            
            cl.Initialize(credentials, channel);
            cl.OnMessageReceived += new EventHandler<OnMessageReceivedArgs>(globalChatMessageReceived);
            cl.OnConnected += new EventHandler<OnConnectedArgs>(onConnected);
            cl.Connect();
            System.Console.WriteLine("Connecting...");
        }

        private static JoinedChannel myjc;

        private static void onConnected(object sender, OnConnectedArgs e)
        {
            System.Console.WriteLine("Connected! Channel: #" + channel);
        }

        private static void about(ChatMessage c)
        {
            verb("About req from " + c.Username);
            cl.SendWhisper(c.Username, "I was programmed by @kraln. You can find more information at my github page: https://github.com/kraln/schickenbot. Want me for your channel? Just ask...");
        }

        private static void help(ChatMessage c)
        {
            verb("Help req from " + c.Username);
            cl.SendMessage(channel, "I respond to the following commands: !points, !leaderboard, !stats, !help, !about, !guess xxxx");
            if (c.IsBroadcaster || c.IsModerator)
            {
                cl.SendMessage(channel, "Mods can also !start, !reset, !end xxxx");
            }
        }

        private static void stats(ChatMessage c)
        {
            verb("Stats req from " +  c.Username);
            long[] stat = stats();
            cl.SendWhisper(c.Username, stat[0] + " viewers, " + stat[1] + " rounds, " + stat[2] + " guesses tracked across " + stat[3] + " channels.");
        }

        private static void round_guess(ChatMessage c)
        {
            verb("guess from " + c.Username);

           
            round_more_guesses = true;
            string guess = "";
            try
            {
                guess = new string(c.Message.Substring(c.Message.IndexOf(" ") + 1).Where(Char.IsLetterOrDigit).ToArray()); // linq magic to extract any leading/trailing chars
            }
            catch (Exception e) { }

            guess = Soundex(guess);
            string user = c.Username;
            long chan_id = get_channel_id(c.Channel);

            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();

                // we track players based on their first guess
                com.CommandText = "INSERT OR IGNORE INTO players (nickname, chan_id) VALUES (@nickname, @chanid)";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@nickname", c.Username);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.ExecuteNonQuery();

                con.Close();

                con.Open();

                // get the userid for this nickname
                com.CommandText = "SELECT id FROM players WHERE nickname = @nickname AND chan_id = @chanid";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@nickname", c.Username);
                com.Parameters.AddWithValue("@chanid", chan_id);
                Object res = com.ExecuteScalar();

                long userId = -1;
                if (res != null)
                {
                    userId = (long)com.ExecuteScalar();
                }
                else
                {
                    verb("Problem with guess from " + c.Username + ". Couldn't find id?");
                    con.Close();
                    return;
                }

                // This is a goofy sqlite upsert
                com.CommandText = @"UPDATE OR IGNORE guesses 
                                    SET guess=@guess, t=CURRENT_TIMESTAMP 
                                    WHERE user_id=@user_id AND round_id=@round_id AND chan_id=@chanid";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@guess", guess);
                com.Parameters.AddWithValue("@user_id", userId);
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.ExecuteNonQuery();

                com.CommandText = "INSERT OR IGNORE INTO guesses (guess, user_id, round_id, chan_id) VALUES (@guess, @user_id, @round_id, @chanid)";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@guess", guess);
                com.Parameters.AddWithValue("@user_id", userId);
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.ExecuteNonQuery();

                con.Close();
            }
        }

        private static void award_points(string user_id, string guess, string actual_guess, long place)
        {
            long new_points = 0;
            round_awarded++;
            switch (place)
            {
                case 0:
                    new_points = 500; // exact guess
                    break;
                case 1:
                    new_points = 50; // first
                    break;
                case 2:
                    new_points = 15; // second
                    break;
                case 3:
                    new_points = 5; // third
                    break;
            }

            string player_name = "<unknown>";
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                // add points to the user
                com.CommandText = "UPDATE players SET points = points + @new_points WHERE id = @id";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@id", user_id);
                com.Parameters.AddWithValue("@new_points", new_points);
                com.ExecuteNonQuery();

                // and get their name
                com.CommandText = "SELECT nickname FROM players WHERE id = @id";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@id", user_id);
                object res = com.ExecuteScalar();
                if (res != null)
                {
                    player_name = (string)res;
                }
            }

            // notify the channel
            switch (place)
            {
                case 0:
                    cl.SendMessage(channel, player_name + " guessed first, and wins " + new_points + " points!");
                    break;
                case 1:
                    cl.SendMessage(channel, player_name + " was the closest second, and wins " + new_points + " points!");
                    break;
                case 2:
                    cl.SendMessage(channel, player_name + " came in third and earns " + new_points + " points.");
                    break;
                case 3:
                    cl.SendMessage(channel, player_name + " had the fourth best guess, earning " + new_points + " points.");
                    break;
            }
        }

        private static void round_end(ChatMessage c)
        {
            string guess = "";
            try
            {
                guess = new string(c.Message.Substring(c.Message.IndexOf(" ") + 1).Where(Char.IsLetterOrDigit).ToArray()); // linq magic to extract any leading/trailing chars
            }
            catch (Exception e)
            { }

            guess = Soundex(guess);
            long chan_id = get_channel_id(c.Channel);

            verb("round ended by " + c.Username + ", with enemy of " + guess); 
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();
                com.CommandText = @"SELECT user_id, guess
                                    FROM guesses
                                    WHERE round_id = @round_id
                                    AND chan_id = @chanid
                                    AND guess LIKE @guess
                                    ORDER BY t ASC LIMIT 4";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.Parameters.AddWithValue("@guess", guess);

                long i = 0;
                using (System.Data.SQLite.SQLiteDataReader r = com.ExecuteReader())
                {
                    while (r.Read() && i < 4)
                    {
                        award_points((string)r["user_id"], (string)r["guess"], guess, i++);
                    }
                }

                // then update the round with the final time, for stats
                com.CommandText = @"UPDATE rounds SET guess = @guess WHERE id = @id AND chan_id = @chanid";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.Parameters.AddWithValue("@guess", guess);
                com.ExecuteNonQuery();

                con.Close();
            }

            if(round_awarded == 0)
            {
                cl.SendMessage(channel, "Round #"+ round_id +" ended without anyone winning :(");
            }
            
            // end the round
            round_started = false;
        }

        private static void round_reset()
        {
            round_started = false;
            cl.SendMessage(channel, "Round #" + round_id + " cancelled.");
            System.Console.WriteLine("Round #" + round_id + " cancelled.");
        }
        public static string Soundex(string data)
        {
            StringBuilder result = new StringBuilder();

            if (data != null && data.Length > 0)
            {
                string previousCode = "", currentCode = "", currentLetter = "";

                result.Append(data.Substring(0, 1));

                for (int i = 1; i < data.Length; i++)
                {
                    currentLetter = data.Substring(i, 1).ToLower();
                    currentCode = "";

                    if ("bfpv".IndexOf(currentLetter) > -1)
                        currentCode = "1";

                    else if ("cgjkqsxz".IndexOf(currentLetter) > -1)
                        currentCode = "2";

                    else if ("dt".IndexOf(currentLetter) > -1)
                        currentCode = "3";

                    else if (currentLetter == "l")
                        currentCode = "4";

                    else if ("mn".IndexOf(currentLetter) > -1)
                        currentCode = "5";

                    else if (currentLetter == "r")
                        currentCode = "6";

                    if (currentCode != previousCode)
                        result.Append(currentCode);

                    if (result.Length == 4) break;
                        previousCode = currentCode;

                }
            }
            if (result.Length < 4)
                result.Append(new String('0', 4 - result.Length));

            return result.ToString().ToUpper();
        }
        private static void round_begin(ChatMessage c)
        {
            long chan_id = get_channel_id(c.Channel);
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();
                com.CommandText = "INSERT INTO rounds (chan_id) VALUES (@chanid)";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.ExecuteNonQuery();
                round_id = con.LastInsertRowId;
                con.Close();
            }
            round_started = true;
            round_awarded = 0;
            round_started_time = DateTime.Now.ToFileTimeUtc();
            cl.SendMessage(channel, "Round #" + round_id + " started. Type !guess xxxx to register your guess at what enemy will kill Barb.");
            System.Console.WriteLine("Round #" + round_id + " started.");
        }

        private static void player_leaderboard(ChatMessage c)
        {

            long chan_id = get_channel_id(c.Channel);
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();

                com.CommandText = @"SELECT nickname, points
                                    FROM players
                                    WHERE chan_id = @chanid
                                    ORDER BY points DESC LIMIT 5";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@chanid", chan_id);
 
                long pos = 1;
                string list = "Leaderboard for #" + c.Channel + ": ";
                using (System.Data.SQLite.SQLiteDataReader r = com.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list = list + " (" + pos + ") " + ((string)r["nickname"]).Trim() + " - " + r["points"] + ", ";
                        pos++;
                    }
                }

                // then tell the player their position
                com.CommandText = @"SELECT count(*) AS rank 
                                    FROM players 
                                    WHERE chan_id = @chanid AND points > (SELECT points from players where nickname = @nickname)
                                    ORDER BY points DESC";

                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@nickname", c.Username);
                com.Parameters.AddWithValue("@chanid", chan_id);
                object res = com.ExecuteScalar();
                long rank = 0;
                if (res != null)
                {
                    rank = (long)res;
                }

                com.CommandText = @"SELECT count(*) from players where chan_id = @chanid"; 
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@chanid", chan_id);
                res = com.ExecuteScalar();
                long total = 0;
                if (res != null)
                {
                    total = (long)res;
                }
               
                con.Close();

                cl.SendWhisper(c.Username, list + " you are ranked " + (rank!=0?rank:total) + "/" + total);
            }
            verb("Leaderboard req from " +  c.Username);
        }

        private static void player_points(ChatMessage c)
        {
            long playerPoints = 0;
            long chan_id = get_channel_id(c.Channel);
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();
                com.CommandText = "SELECT points FROM players WHERE nickname = @nickname AND chan_id = @chanid";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@nickname", c.Username);
                com.Parameters.AddWithValue("@chanid", chan_id);
                object res = com.ExecuteScalar();
                if (res != null)
                {
                    playerPoints = (long)res;
                }
                con.Close();
            }
            cl.SendWhisper(c.Username, "You have " + playerPoints + " points in #" + c.Channel.Trim() + ".");
            verb("Points req from " +  c.Username);
        }

        private static void verb(String s)
        {
            if(true)
            System.Console.WriteLine(s);
        }

        private static void ensure_channel(ChatMessage c)
        {
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();
                com.CommandText = "INSERT OR IGNORE INTO channels (channel_name) VALUES (@channel)";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@channel", c.Channel);
                com.ExecuteNonQuery();
                con.Close();
            }

        }

        private static long get_channel_id(string name)
        {
            long chan_id = -1;

            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();
                com.CommandText = "SELECT ID FROM channels WHERE channel_name=@channel_name";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@channel_name", name);
                Object res = com.ExecuteScalar();
                if(res!= null)
                {
                    chan_id = (long)res;
                }
                con.Close();
            }

            return chan_id;
        }

        private static void globalChatMessageReceived(object sender, OnMessageReceivedArgs e)
        {
            /* TODO: command registration? modules? etc? */

            // make sure there is a channel entry for this place
            if (e.ChatMessage.Message.StartsWith("!"))
            {
                ensure_channel(e.ChatMessage);
            }

            if (round_started)
            {
                // guess
                if(e.ChatMessage.Message.StartsWith("!guess"))
                {
                    round_guess(e.ChatMessage);
                }

                // end (MOD)
                if(e.ChatMessage.Message.StartsWith("!end") && (e.ChatMessage.IsModerator || e.ChatMessage.IsBroadcaster))
                {
                    round_end(e.ChatMessage);
                }

                // reset (MOD)
                if(e.ChatMessage.Message.StartsWith("!reset") && (e.ChatMessage.IsModerator || e.ChatMessage.IsBroadcaster))
                {
                    round_reset();
                }
            }
            else
            {
                // start (MOD)
                if(e.ChatMessage.Message.StartsWith("!start") && (e.ChatMessage.IsModerator || e.ChatMessage.IsBroadcaster))
                {
                    round_begin(e.ChatMessage);
                }
            }

            // points (whisper resp)
            if(e.ChatMessage.Message.StartsWith("!points"))
            {
                player_points(e.ChatMessage);
            }

            // leaderboard (whisper resp)
            if(e.ChatMessage.Message.StartsWith("!leaderboard"))
            {
                player_leaderboard(e.ChatMessage);
            }

            // stats  (whisper resp)
            if(e.ChatMessage.Message.StartsWith("!stats"))
            {
                stats(e.ChatMessage);
            }

            // help (in-chan response)
            if(e.ChatMessage.Message.StartsWith("!help"))
            {
                help(e.ChatMessage);
            }
            
            // about (whisper resp)
            if(e.ChatMessage.Message.StartsWith("!about"))
            {
                about(e.ChatMessage);
            }
        }
    }
}
