using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib;
using TwitchLib.Events.Client;
using TwitchLib.Models.Client;

namespace SwedishCeresBot
{
    class Program
    {
        public static string user = "shickenbot";
        public static string oauth = "";
        public static string channel = "kottpower";

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
                                            [time] INTEGER DEFAULT 0,
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
                                            [time] INTEGER NOT NULL,
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
            cl = new TwitchClient(credentials, channel, '!', '!', true, false);
            cl.SetLoggingStatus(false);
            cl.ChatThrottler = new TwitchLib.Services.MessageThrottler(5, TimeSpan.FromSeconds(60));
            cl.OnMessageReceived += new EventHandler<OnMessageReceivedArgs>(globalChatMessageReceived);
            cl.OnConnected += new EventHandler<OnConnectedArgs>(onConnected);
            cl.Connect();
            System.Console.WriteLine("Connecting...");
        }

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
            cl.SendMessage("I respond to the following commands: !points, !leaderboard, !stats, !help, !about, !guess xxxx");
            if (c.IsBroadcaster || c.IsModerator)
            {
                cl.SendMessage("Mods can also !start, !reset, !end xxxx");
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

            // check to make sure the game hasn't been running too long
            if (Math.Abs(DateTime.Now.ToFileTimeUtc() - round_started_time) > 45 * 10000000L)
            {
                cl.SendWhisper(c.Username, "Sorry, it's been more than 45 seconds. Try next round!");
                if(round_more_guesses)
                {
                    round_more_guesses = false;
                    cl.SendMessage("Guessing is now over, please wait until the next round.");
                }
                return;
            }

            round_more_guesses = true;
            string guess = new string(c.Message.Where(Char.IsDigit).ToArray()); // linq magic to extract any leading/trailing chars
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
                                    SET time=@guess, t=CURRENT_TIMESTAMP 
                                    WHERE user_id=@user_id AND round_id=@round_id AND chan_id=@chanid";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@guess", guess);
                com.Parameters.AddWithValue("@user_id", userId);
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.ExecuteNonQuery();

                com.CommandText = "INSERT OR IGNORE INTO guesses (time, user_id, round_id, chan_id) VALUES (@guess, @user_id, @round_id, @chanid)";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@guess", guess);
                com.Parameters.AddWithValue("@user_id", userId);
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.ExecuteNonQuery();

                con.Close();
            }
        }

        private static void award_points(string user_id, long guess, string endtime, long place)
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
                    cl.SendMessage(player_name + " guessed exactly, and wins " + new_points + " points!");
                    break;
                case 1:
                    cl.SendMessage(player_name + " was the closest, and wins " + new_points + " points!");
                    break;
                case 2:
                    cl.SendMessage(player_name + " came in second and earns " + new_points + " points.");
                    break;
                case 3:
                    cl.SendMessage(player_name + " had the third best guess, earning " + new_points + " points.");
                    break;
            }
        }

        private static void round_end(ChatMessage c)
        {
            string endtime = new string(c.Message.Where(Char.IsDigit).ToArray()); // linq magic to extract any leading/trailing chars

            if(endtime.Length != 4)
            {
                verb("Invalid endtime (" + endtime + ")");
                return;
            }

            long chan_id = get_channel_id(c.Channel);

            verb("round ended by " + c.Username + ", with time of " + endtime); 
            using (System.Data.SQLite.SQLiteCommand com = new System.Data.SQLite.SQLiteCommand(con))
            {
                con.Open();

               // first, all the perfect guesses
                com.CommandText = @"SELECT user_id, time 
                                    FROM guesses
                                    WHERE round_id = @round_id
                                    AND chan_id = @chanid
                                    AND time = @end_time";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.Parameters.AddWithValue("@end_time", endtime);

                using (System.Data.SQLite.SQLiteDataReader r = com.ExecuteReader())
                {
                    while (r.Read())
                    {
                        award_points((string)r["user_id"], (long)r["time"], endtime, 0);
                    }
                }

                // then, all the users who weren't exactly right
                com.CommandText = @"SELECT user_id, time 
                                    FROM guesses
                                    WHERE round_id = @round_id
                                    AND time != @end_time
                                    AND chan_id = @chanid
                                    ORDER BY ABS(time - @end_time) ASC LIMIT 3";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@round_id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.Parameters.AddWithValue("@end_time", endtime);

                using (System.Data.SQLite.SQLiteDataReader r = com.ExecuteReader())
                {
                    long place = 1;
                    while (r.Read())
                    {
                        award_points((string)r["user_id"], (long)r["time"], endtime, place);
                        place++;
                    }
                }

                // then update the round with the final time, for stats
                com.CommandText = @"UPDATE rounds SET time = @end_time WHERE id = @id AND chan_id = @chanid";
                com.CommandType = System.Data.CommandType.Text;
                com.Parameters.AddWithValue("@id", round_id);
                com.Parameters.AddWithValue("@chanid", chan_id);
                com.Parameters.AddWithValue("@end_time", endtime);
                com.ExecuteNonQuery();

                con.Close();
            }

            if(round_awarded == 0)
            {
                cl.SendMessage("Round #"+ round_id +" ended without anyone playing :(");
            }
            
            // end the round
            round_started = false;
        }

        private static void round_reset()
        {
            round_started = false;
            cl.SendMessage("Round #" + round_id + " cancelled.");
            System.Console.WriteLine("Round #" + round_id + " cancelled.");
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
            cl.SendMessage("Round #" + round_id + " started. Type !guess xxxx to register your Ceres time. You have 45 seconds to place your guess.");
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
