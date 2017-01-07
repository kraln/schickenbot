# SchickenBot
Ceresbot Clone for Twitch Streamer Kottpower

The bot keeps all the info in a sqlite database which can later be used for generating interesting statistics (averages, graphs, stuff). The info is separated by channel, so if you use the same bot for different channels they shouldn't interfere. Uses the excellent TwitchLib. You'll need an oauth token for the bot user. 

## Commands
### Before a round:
 * !start (mod or streamer only)

### During a round:
 * !guess xxxx
 * !end xxxx (mod or streamer only)
 * !reset (mod or streamer only)

(extra characters are stripped from guesses and endtimes)

### Anytime:

* !points (response is whispered)
* !leaderboard (response is whispered)
* !stats (response is whispered)


License: BSD 2-clause
