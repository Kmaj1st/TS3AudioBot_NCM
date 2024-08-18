using NeteaseApiData;
using NLog.Fluent;
using Org.BouncyCastle.Bcpg.OpenPgp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using TS3AudioBot.ResourceFactories;

public enum Mode
{
    SeqPlay = 0,
    SeqLoopPlay = 1,
    RandomPlay = 2,
    RandomLoopPlay = 3,
}

public class PlayListMeta
{
    public string? NCMId;
    public string? Name;
    public string? Image;
	public string? DirectUrl;

    public PlayListMeta(string? id, string? name, string? image, string? directUrl = null)
    {
        NCMId = id;
        Name = name;
        Image = image;
		DirectUrl = directUrl;
    }
}

public class MusicInfo
{
    public string? NCMId = "";
    public string? Name = "";
    public string? Image = "";
    public string? DetailUrl = "";
    public bool InPlayList;
	public string? DirectUrl;

	private Dictionary<string, int?> Author = new Dictionary<string, int?>();

	public MusicInfo() { }

    public MusicInfo(string? id, string? directUrl = null, bool inPlayList = true)
    {
        NCMId = id;
        InPlayList = inPlayList;
		DirectUrl = directUrl;
	}

	public MusicInfo(string? id, string? name, string? image, string? detailUrl, string? directUrl = null, bool inPlayList = true)
    {
        NCMId = id;
        Name = name;
        Image = image;
        DetailUrl = detailUrl;
        InPlayList = inPlayList;
		DirectUrl = directUrl;
    }

	public string GetArtist()
    {
		if (DirectUrl != null)
		{
			if (!File.Exists(DirectUrl)) return "无法获取艺术家信息";
			TagLib.File tagFile = TagLib.File.Create(DirectUrl);
			return tagFile.Tag.FirstAlbumArtist;
		}
        return string.Join(" / ", Author.Keys);
    }

    public string GetFullName()
	{
		var artist = GetArtist();
        artist = !string.IsNullOrEmpty(artist) ? $" - {artist}" : "";
		if (DirectUrl != null)
		{
			if (string.IsNullOrEmpty(Name))
			{
				if (File.Exists(DirectUrl))
				{
					TagLib.File tagFile = TagLib.File.Create(DirectUrl);
					string localFileSongTitle = tagFile.Tag.Title;
					Name = !string.IsNullOrEmpty(localFileSongTitle) ? localFileSongTitle : GetURLFileName();
				} else
				{
					Name = GetURLFileName();
				}
			}
		}
        return Name + artist;
    }

	public string GetURLFileName()
	{
		string fn;
		if (DirectUrl is null) return "null";
		if (File.Exists(DirectUrl))
		{
			fn = Path.GetFileNameWithoutExtension(DirectUrl);
			Log.Info($"Using file name ({fn}) instead of song name");
		} else
		{
			string[] strings = DirectUrl.Split("/");
			fn = strings[^1];
		}

		return fn;
	}

	public string GetFullNameBBCode()
    {
		if (DirectUrl != null)
		{
			return GetFullName();
		}
		var artist = GetAuthorBBCode();
        artist = !string.IsNullOrEmpty(artist) ? $" - {artist}" : "";
        return $"[URL={DetailUrl}]{Name}[/URL]{artist}";
    }

    public string GetAuthorBBCode()
    {
		if (DirectUrl != null)
		{
			return GetArtist();
		}
		return string.Join(" / ", Author.Select(entry =>
        {
            string key = entry.Key;
            int? id = entry.Value;
            string authorName = id == null ? key : $"[URL=https://music.163.com/#/artist?id={id}]{key}[/URL]";
            return authorName;
        }));
    }

    public async Task<AudioResource> GetAudioResource()
    {
		if (await Utils.IsValidPathOrFile(DirectUrl))
		{
			return new AudioResource(DirectUrl, GetFullName(), "media");
		}
		return new AudioResource(DetailUrl, GetFullName(), "media").Add("PlayUri", Image);
    }

    public async Task<byte[]> GetImage()
	{
		string image;
		if (String.IsNullOrEmpty(Image))
		{
			image = "https://nyanneko.com/favicon.ico";
		} else
		{
			image = Image;
		}
		var request = (HttpWebRequest)WebRequest.Create(image);
		request.Method = "GET";

		using var response = (HttpWebResponse)await request.GetResponseAsync();
		using Stream stream = response.GetResponseStream();
		using var memoryStream = new MemoryStream();
		await stream.CopyToAsync(memoryStream);
		return memoryStream.ToArray();
	}

    public async Task InitMusicInfo(string? api, Dictionary<string, string>? header = null)
    {
		if (!String.IsNullOrEmpty(DirectUrl) || !String.IsNullOrEmpty(Name)) return;

		if (api is null)
		{
			Log.Error("[MusicInfo.InitMusicInfo] neteaseApi is null");
			return;
		}
		if (header is null)
		{
			Log.Error("[MusicInfo.InitMusicInfo] header is null");
			return;
		}
        string musicdetailurl = $"{api}/song/detail?ids={NCMId}&t={Utils.GetTimeStamp()}";
        JsonSongDetail musicDetail = await Utils.HttpGetAsync<JsonSongDetail>(musicdetailurl, header);

		if (musicDetail.songs is null || musicDetail.songs.Length == 0)
		{
			Log.Error("[MusicInfo.InitMusicInfo] songs is null or 0 length");
			return;
		}
		if (musicDetail.songs[0].al != null)
		{
			Image = musicDetail.songs[0].al?.picUrl;
		} else
		{
			Image = null;
		}
        Name = musicDetail.songs[0].name;
		Name ??= "_";
        DetailUrl = $"https://music.163.com/#/song?id={NCMId}";

        Author.Clear();

        var artists = musicDetail.songs[0].ar;
        if (artists != null)
        {
            foreach (var artist in artists)
            {
                if (!string.IsNullOrEmpty(artist.name))
                {
                    Author.Add(artist.name, artist.id);
                }
            }
        }
    }

    public async Task<string?> GetURL(string api, Dictionary<string, string> header)
    {
		if (DirectUrl != null)
		{
			return DirectUrl;			
		}

		string api_url = $"{api}/song/url?id={NCMId}&t={Utils.GetTimeStamp()}";

        try
        {
            MusicURL musicURL = await Utils.HttpGetAsync<MusicURL>(api_url, header);
			if (musicURL is null || musicURL.data[0] is null)
			{
				Log.Error("[MusicInfo.GetNCMMusicURL] musicURL is null");
				return "musicURL is null";
			}

            return musicURL.data[0].url;
        }
        catch (Exception e)
        {
			Log.Error($"Get music url error: {api_url}");
			throw e;
        }
    }
}
