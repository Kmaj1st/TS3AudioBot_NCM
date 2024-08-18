// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using NeteaseApiData;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Messages;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace TS3AudioBot.Audio
{
	/// <summary>Provides a convenient inferface for enqueing, playing and registering song events.</summary>
	public class PlayManager
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private readonly ConfBot confBot;
		public Player PlayerConnection { get; private set; }
		private readonly PlaylistManager playlistManager;
		private readonly ResolveContext resourceResolver;
		private readonly Stats stats;

		public PlayInfoEventArgs? CurrentPlayData { get; private set; }
		public bool IsPlaying => CurrentPlayData != null;

		public event AsyncEventHandler<PlayInfoEventArgs>? OnResourceUpdated;
		public event AsyncEventHandler<PlayInfoEventArgs>? BeforeResourceStarted;
		public event AsyncEventHandler<PlayInfoEventArgs>? AfterResourceStarted;
		public event AsyncEventHandler<SongEndEventArgs>? ResourceStopped;
		public event AsyncEventHandler? PlaybackStopped;

		private readonly List<ulong> ownChannelClients = new List<ulong>();
		private ulong? ownChannelID;
		private readonly SemaphoreSlim slimlock = new SemaphoreSlim(1, 1);
		private System.Threading.Timer? timer;
		private Ts3Client? ts3Client;

		public MusicInfo? CurrentMusicInfo { get; private set; }
		private int currentPlay;
		public Dictionary<string, string>? Header { get; private set; }
		public List<MusicInfo> MusicInfoList { get; set; } = new List<MusicInfo>();
		public Mode Mode { get; private set; }
		public string? NeteaseAPI { get; private set; }
		public InvokerData? Invoker { get; set; }
		public PlayListMeta? PlayListMeta { get; set; }
		public TsFullClient? Ts3FullClient { get; private set; }
		public YunConfig? yunConf;

		readonly string SongListsDir = "./songlists/";
		readonly string DeletedDirectoryDir = "./songlists/deleted/";

		public PlayManager(ConfBot config, Player playerConnection, PlaylistManager playlistManager, ResolveContext resourceResolver, Stats stats)
		{
			confBot = config;
			this.PlayerConnection = playerConnection;
			this.playlistManager = playlistManager;
			this.resourceResolver = resourceResolver;
			this.stats = stats;
		}

		public async Task Add(string link, string? audioType = null, PlayInfo? meta = null)
		{
			if (ts3Client is null)
			{
				Log.Fatal("[PlayManager.SetAvatarAndDesc] ts3Client is null");
				return;
			}

			MusicInfo? musicInfo = await GetMusicInfo(link, meta);
			if (musicInfo is null)
			{
				Log.Warn($"[PlayManager.Play] musicInfo (of {link}) is null");
				return;
			}
			//AddMusic(musicInfo, false);
			AddMusic(musicInfo);
		}
		public async Task<string> AddFolder(string folder)
		{
			string[] files = Directory.GetFiles(folder);

			foreach (string file in files)
			{
				await Add(file);
			}
			return $"已添加{Path.GetFileName(folder)} [{files.Length}]";
		}

		public void AddMusic(MusicInfo musicInfo, bool insert = false, int num = 0)
		{
			MusicInfoList.RemoveAll( m => (m.NCMId != null && m.NCMId == musicInfo.NCMId) || (m.DetailUrl != null && m.DetailUrl == musicInfo.DirectUrl) );
			if (insert)
				MusicInfoList.Insert(num, musicInfo);
			else
				MusicInfoList.Add(musicInfo);
		}

		private void CheckOwnChannel()
		{
			if (yunConf is null)
			{
				Log.Error("[PlayManager.CheckOwnChannel] yunConf is null");
				return;
			}
			if (!yunConf.autoPause)
			{
				return;
			}
			if (ownChannelClients.Count < 1)
			{
				PlayerConnection.Paused = true;
			}
			else
			{
				PlayerConnection.Paused = false;
			}
			Log.Info("ownChannelClients: {}", ownChannelClients.Count);
		}





		public Task Enqueue(InvokerData invoker, AudioResource ar, PlayInfo? meta = null) => Enqueue(invoker, new PlaylistItem(ar, meta));
		public async Task Enqueue(InvokerData invoker, string message, string? audioType = null, PlayInfo? meta = null)
		{
			PlayResource? playResource;
			try { playResource = await resourceResolver.Load(message, audioType); }
			catch
			{
				stats.TrackSongLoad(audioType, false, true);
				throw;
			}
			await Enqueue(invoker, PlaylistItem.From(playResource).MergeMeta(meta));
		}
		public Task Enqueue(InvokerData invoker, IEnumerable<PlaylistItem> items)
		{
			var startOff = playlistManager.CurrentList.Items.Count;
			playlistManager.Queue(items.Select(x => UpdateItem(invoker, x)));
			return PostEnqueue(invoker, startOff);
		}
		public Task Enqueue(InvokerData invoker, PlaylistItem item)
		{
			var startOff = playlistManager.CurrentList.Items.Count;
			playlistManager.Queue(UpdateItem(invoker, item));
			return PostEnqueue(invoker, startOff);
		}

		public async Task<string> Export(string filename)
		{
			if (String.IsNullOrEmpty(filename)) return "导出歌单: !export [歌单名]";
			try
			{
				string filePath = SongListsDir + filename + ".json";

				string deletedFilePath = DeletedDirectoryDir + filename + ".json";

				if (!Directory.Exists(SongListsDir))
				{
					Directory.CreateDirectory(SongListsDir);
				}
				if (!Directory.Exists(DeletedDirectoryDir))
				{
					Directory.CreateDirectory(DeletedDirectoryDir);
				}

				if (File.Exists(filePath))
				{
					if (File.Exists(deletedFilePath))
					{
						File.Delete(deletedFilePath);
					}
					File.Move(filePath, deletedFilePath);
				}
				
				if (MusicInfoList == null || MusicInfoList.Count == 0)
				{
					return "歌单为空, 拒绝导出";
				}

				var exportData = new
				{
					Meta = PlayListMeta,
					MusicList = MusicInfoList
				};

				string jsonString = JsonConvert.SerializeObject(exportData);

				await Task.Run(() =>
				{
					File.WriteAllText(filePath, jsonString);
				});

				return "已导出" + filename;
			}
			catch (Exception ex)
			{
				return $"Error: {ex.Message}";
			}
		}

		public async Task<MusicInfo?> GetMusicInfo(string musicStr, PlayInfo? meta = null)
		{
			if (ts3Client is null)
			{
				throw new NullReferenceException(nameof(Ts3Client));
			}
			if (musicStr.Contains("#"))
			{
				await ts3Client.SendChannelMessage("在歌名中检测到\"#\", 如需选择搜索模式请使用![指令] [歌名] [模式]");
			}

			MusicInfo? mInfo;
			if (meta != null && meta.AudioSource != null)
			{
				mInfo = meta.AudioSource switch
				{
					PlayInfo.Source.NCM => await GetMusicInfoNCM(musicStr),
					_ => await GetMusicInfoDefaultAsync(musicStr),
				};
			} else
			{
				mInfo = await GetMusicInfoDefaultAsync(musicStr);
				if (mInfo is null)
				{
					await ts3Client.SendChannelMessage("播放本地文件失败，正在使用网易云音乐搜素");
					mInfo = await GetMusicInfoNCM(musicStr);
				}
			}
			if (mInfo is null)
			{
				string metaStr = "null";
				if (meta != null && meta.AudioSource != null)
				{
					metaStr = meta.AudioSource.ToString();
				}
				Log.Warn($"[PlayManager.Play] ({metaStr}) musicInfo is null");
				return null;
			}
			return mInfo;
		}

		public async Task<MusicInfo> GetMusicInfoNCM(string args = "")
		{
			Log.Info($"GetResourceNCM {args}");
			string? songID = Utils.ExtractIdFromAddress(args);
			if (!Utils.IsNumber(songID))
			{
				string? urlSearch = $"{NeteaseAPI}/search?keywords={args}&limit=1";
				YunSearchSong? yunSearchSong = await Utils.HttpGetAsync<YunSearchSong>(urlSearch);
				if (yunSearchSong.result.songs.Length == 0)
				{
					if (ts3Client is null)
					{
						Log.Info("未找到歌曲");
					} else
					{
						await ts3Client.SendChannelMessage("未找到歌曲");
					}
				}
				songID = yunSearchSong.result.songs[0].id.ToString();
				Log.Info($"songID {songID}");
			}
			var mInfo = new MusicInfo(songID, null, false);
			//AddMusic(mInfo, false);
			/*
			MediaPlayResource? playObject =  await GetPlayObject(music);
			if (playObject is null)
			{
				Log.Error("[PlayManager.GetResourceNCM] playObject is null");
				throw new NullReferenceException();
			}
			*/
			return mInfo;
		}

		private async Task<MusicInfo?> GetMusicInfoDefaultAsync(string? arg = "")
		{
			if (ts3Client is null) throw new NullReferenceException(nameof(ts3Client));
			arg = Utils.ConvertToPlainUrl(arg);
			if (String.IsNullOrWhiteSpace(arg)) return null;
			if(await Utils.IsValidPathOrFile(arg)) {
				return new MusicInfo(null, arg, false);
			}
			
			if (Utils.IsNumber(arg))
			{
				int num = int.Parse(arg);
				if (num >= 0 && num < MusicInfoList.Count)
				{
					return MusicInfoList[num - 1];
				} else
				{
					if (ts3Client != null)
					{
						await ts3Client.SendChannelMessage("数字过大，播放清单中没有这个数字");
						await ts3Client.SendChannelMessage("如果输入的数字为网易云音乐id, 请使用 ![指令] [音乐id] #n");
						return null;
					}
				}
			}

			return null;
		}

		public async Task Init(TsFullClient ts3FullClient, Ts3Client ts3client, Player player)
		{
			string msg;
			try
			{
				List<string> joiningMsg = new List<string>(File.ReadAllLines("config/joiningmsg.txt"));		
				Random random = new Random();
				int index = random.Next(joiningMsg.Count);
				msg = joiningMsg[index];
			}
			catch
			{
				msg = "Hello World";
			}

			LoadNCMConfig();

			this.Ts3FullClient = ts3FullClient;
			this.ts3Client = ts3client;
			this.PlayerConnection = player;

			if (this.Ts3FullClient is null)
			{
				Log.Error("failed to add EventHandler for TSFullCliet");
				return;
			} else
			{
				ts3FullClient.OnEachClientLeftView += OnEachClientLeftView;
				ts3FullClient.OnEachClientEnterView += OnEachClientEnterView;
				ts3FullClient.OnEachClientMoved += OnEachClientMoved;
			}

			await ts3Client.SendChannelMessage(msg);
		}

		public async Task<string> Import(string filename)
		{
			try
			{
				string filePath = SongListsDir + filename + ".json";
				var list = new List<MusicInfo>();
				PlayListMeta? meta = null;

				if (!File.Exists(filePath))
				{
					return "无法找到歌单文件";
				}

				await Task.Run(() =>
				{
					try
					{
						string jsonString = File.ReadAllText(filePath);
						var importData = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);

						if (importData != null)
						{
#pragma warning disable CS8604 // Possible null reference argument.
							meta = importData["Meta"] is null ? null : JsonConvert.DeserializeObject<PlayListMeta?>(importData["Meta"].ToString());

							list = JsonConvert.DeserializeObject<List<MusicInfo>>(importData["MusicList"].ToString());
#pragma warning restore CS8604 // Possible null reference argument.
						}
						else
						{
							Log.Warn("[PlayManager.Import] importData is null.");
						}
					}
					catch (Exception ex)
					{
						Log.Error($"[PlayManager.Import] {ex}");
					}
				});

				PlayListMeta = meta;
				MusicInfoList = list;

				return "已导入" + filename;
			}
			catch (Exception e)
			{
				Log.Error($"[PlayManager.Import] {e}");
				return "哎呀, 导入过程中出现了一点小问题xD";
			}
		}

		public string Lists(string? initial = null)
		{
			string[] files;
			try
			{
				files = Directory.GetFiles(SongListsDir);
				for (int i = 0; i < files.Length; i++)
				{
					files[i] = Path.GetFileNameWithoutExtension(files[i]);
				}
			}
			catch
			{
				return "无法找到目录";
			}
			string str = "";

			foreach (string file in files)
			{
				if (!(initial is null) && !file.ToLower().StartsWith(initial))
				{
					continue;
				}
				str += $"{file} ";
			}

			if (string.IsNullOrEmpty(initial))
			{
				return $"已保存的歌单有: {str}";
			} else
			{
				return $"已保存的歌单(以{initial}开头)有: {str}";
			}
		}

		private void LoadNCMConfig()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				string? dockerEnvFilePath = "/.dockerenv";

				if (System.IO.File.Exists(dockerEnvFilePath))
				{
					Log.Info("Docker env detected");
				}
				else
				{
					Log.Info("Linux env detected");
				}
				Assembly? assembly = Assembly.GetEntryAssembly();
				if (assembly is null)
				{
					Log.Error("[PlayManager.LoadNCMConfig] assembly is null");
					return;
				}
				string? location = Path.GetDirectoryName(assembly.Location);
				yunConf = YunConfig.GetConfig(System.IO.File.Exists(dockerEnvFilePath) ? location + "/data/config/NCMSettings.yml" : location + "/config/NCMSettings.yml");
			}
			else
			{
				yunConf = YunConfig.GetConfig("config/NCMSettings.yml");
			}


			Header = yunConf.Header;
			try
			{
				Mode = (Mode)yunConf.playMode;
			}
			catch (Exception e)
			{
				Log.Warn($"Get play mode error!{e}");
				Mode = Mode.SeqPlay;
			}

			 NeteaseAPI = yunConf.neteaseApi;


			timer?.Dispose();

			if (yunConf.cookieUpdateIntervalMin <= 0)
			{
				timer = new System.Threading.Timer(async (e) =>
				{
					if (!yunConf.isQrlogin && Header.ContainsKey("Cookie") && !string.IsNullOrEmpty(Header["Cookie"]))
					{
						try
						{
							string? url = $"{NeteaseAPI}/login/refresh?t={Utils.GetTimeStamp()}";
							Status1? status = await Utils.HttpGetAsync<Status1>(url, Header);
							if (status.code == 200)
							{
								var newCookie = Utils.MergeCookie(Header["Cookie"], status.cookie);
								ChangeCookies(newCookie, false);
								Log.Info("Cookie update success");
							}
							else
							{
								Log.Warn("Cookie update failed");
							}
						}
						catch (Exception ex)
						{
							Log.Error(ex, "Cookie update error");
						}
					}
				}, null, TimeSpan.Zero.Milliseconds, TimeSpan.FromMinutes(yunConf.cookieUpdateIntervalMin).Milliseconds);
			}

			Log.Info("Yun Plugin loaded");
			Log.Info($"Play mode: {Mode}");
			for (int i = 0; i < Header.Count; i++)
			{
				Log.Info($"Header: {Header.Keys.ElementAt(i)}: {Header.Values.ElementAt(i)}");
			}
			Log.Info($"Api address: {NeteaseAPI}");
			if (yunConf.cookieUpdateIntervalMin <= 0)
			{
				Log.Info("Cookie update disabled");
			}
			else
			{
				Log.Info($"Cookie update interval: {yunConf.cookieUpdateIntervalMin} min");
			}
		}

		private async void OnEachClientMoved(object? sender, ClientMoved e)
		{
			if (sender is null)
			{
				Log.Error("[PlayManager.OnEachClientMoved] sender is null");
				return;
			}
			if (Ts3FullClient is null)
			{
				Log.Error("[PlayManager.OnEachClientMoved] Ts3FullClient is null");
				return;
			}
			if (e.ClientId == Ts3FullClient.ClientId)
			{
				await UpdateOwnChannel(e.TargetChannelId.Value);
				return;
			}
			var hasClient = ownChannelClients.Contains(e.ClientId.Value);
			if (e.TargetChannelId.Value == ownChannelID)
			{
				if (!hasClient) ownChannelClients.Add(e.ClientId.Value);
				CheckOwnChannel();
			}
			else if (hasClient)
			{
				ownChannelClients.Remove(e.ClientId.Value);
				CheckOwnChannel();
			}
		}

		private void OnEachClientEnterView(object? sender, ClientEnterView e)
		{
			if (sender is null)
			{
				Log.Error("[PlayManager.OnEachClientEnterView] sender is null");
				return;
			}
			if (Ts3FullClient is null)
			{
				Log.Error("[PlayManager.OnEachClientEnterView] Ts3FullClient is null");
				return;
			}
			if (e.ClientId == Ts3FullClient.ClientId) return;
			if (e.TargetChannelId.Value == ownChannelID) ownChannelClients.Add(e.ClientId.Value);
			CheckOwnChannel();
		}

		private void OnEachClientLeftView(object? sender, ClientLeftView e)
		{
			if (sender is null)
			{
				Log.Error("[PlayManager.OnEachClientLeftView] sender is null");
				return;
			}
			if (Ts3FullClient is null)
			{
				Log.Error("[PlayManager.OnEachClientLeftView] Ts3FullClient is null");
				return;
			}
			if (e.ClientId == Ts3FullClient.ClientId) return;
			if (e.SourceChannelId.Value == ownChannelID) ownChannelClients.Remove(e.ClientId.Value);
			CheckOwnChannel();
		}

		public static PlayInfo? ParseAttributes(string[] attrs)
		{
			if (attrs is null || attrs.Length == 0)
				return null;

			var meta = new PlayInfo();
			foreach (var attr in attrs)
			{
				Log.Info(attr);
				if (attr.StartsWith("@"))
				{
					meta.StartOffset = TextUtil.ParseTime(attr[1..]);
				}
				if (attr.StartsWith("#"))
				{
					meta.AudioSource = PlayInfo.ToAudioSource(attr[1..]);
				}
			}
			Log.Info(meta.ToString());
			return meta;
		}

		private Task PlayManager_AfterResourceStarted(object sender, PlayInfoEventArgs value)
		{
			this.Invoker = value.Invoker;
			return Task.CompletedTask;
		}

		public async Task Play(InvokerData invoker, string arg, string? audioType = null, PlayInfo? meta = null)
		{
			this.Invoker = invoker;

			if (ts3Client is null)
			{
				Log.Fatal("[PlayManager.SetAvatarAndDesc] ts3Client is null");
				throw new ArgumentNullException(nameof(ts3Client));
			}

			MusicInfo? musicInfo = await GetMusicInfo(arg, meta);
			if (musicInfo is null) { return; }

			//AddMusic(musicInfo, true);
			await PlayMusic(musicInfo);
		}

		public async Task Play(InvokerData invoker, AudioResource ar, PlayInfo? meta = null)
		{
			if (ar is null)
				throw new ArgumentNullException(nameof(ar));

			PlayResource? playResource;
			try { playResource = await resourceResolver.Load(ar); }
			catch
			{
				stats.TrackSongLoad(ar.AudioType, false, true);
				throw;
			}
			await Play(invoker, playResource.MergeMeta(meta));
		}

		public Task Play(InvokerData invoker, IEnumerable<PlaylistItem> items, int index = 0)
		{
			playlistManager.Clear();
			playlistManager.Queue(items.Select(x => UpdateItem(invoker, x)));
			playlistManager.Index = index;
			return StartCurrent(invoker);
		}
		public Task Play(InvokerData? invoker, PlayResource? play)
		{
			if (invoker is null) throw new ArgumentNullException(nameof(invoker));
			if (play is null) throw new ArgumentNullException(nameof(play));
			playlistManager.Clear();
			playlistManager.Queue(PlaylistItem.From(play));
			playlistManager.Index = 0;
			stats.TrackSongLoad(play.AudioResource.AudioType, true, true);
			return StartResource(invoker, play);
		}
		public Task Play(InvokerData invoker, PlaylistItem item)
		{
			if (item is null)
				throw new ArgumentNullException(nameof(item));

			if (item.AudioResource is null)
				throw new Exception("Invalid playlist item");
			playlistManager.Clear();
			playlistManager.Queue(item);
			playlistManager.Index = 0;
			return StartResource(invoker, item);
		}
		public Task Play(InvokerData invoker) => StartCurrent(invoker);

		private async Task PostEnqueue(InvokerData invoker, int startIndex)
		{
			if (IsPlaying)
				return;
			playlistManager.Index = startIndex;
			await StartCurrent(invoker);
		}

		public async Task Previous(InvokerData invoker, bool manually = true)
		{
			PlaylistItem? pli = null;
			for (int? i = 0; i < 10; i++)
			{
				pli = playlistManager.Previous(manually);
				if (pli is null) break;
				try
				{
					await StartResource(invoker, pli);
					return;
				}
				catch (AudioBotException ex) { Log.Warn("Skipping: {0} because {1}", pli, ex.Message); }
			}
			if (pli is null)
				throw Error.LocalStr(strings.info_playmgr_no_previous_song);
			else
				throw Error.LocalStr(string.Format(strings.error_playmgr_many_songs_failed, "!previous"));
		}

		public async Task SongStoppedEvent(object? sender, EventArgs e) => await StopInternal(true);

		private async Task StartResource(InvokerData invoker, PlaylistItem item)
		{
			PlayResource? playResource;
			try { playResource = await resourceResolver.Load(item.AudioResource); }
			catch
			{
				stats.TrackSongLoad(item.AudioResource.AudioType, false, false);
				throw;
			}
			stats.TrackSongLoad(item.AudioResource.AudioType, true, false);
			await StartResource(invoker, playResource.MergeMeta(item.PlayInfo));
		}

		private async Task StartResource(InvokerData invoker, PlayResource play)
		{
			var sourceLink = resourceResolver.RestoreLink(play.AudioResource);
			var playInfo = new PlayInfoEventArgs(invoker, play, sourceLink);
			await BeforeResourceStarted.InvokeAsync(this, playInfo);

			if (string.IsNullOrWhiteSpace(play.PlayUri))
			{
				Log.Error("Internal resource error: link is empty (resource:{0})", play);
				throw Error.LocalStr(strings.error_playmgr_internal_error);
			}

			Log.Debug("AudioResource start: {0}", play);
			try { await PlayerConnection.Play(play); }
			catch (AudioBotException ex)
			{
				Log.Error("Error return from player: {0}", ex.Message);
				throw Error.Exception(ex).LocalStr(strings.error_playmgr_internal_error);
			}

			PlayerConnection.Volume = Tools.Clamp(PlayerConnection.Volume, confBot.Audio.Volume.Min, confBot.Audio.Volume.Max);
			CurrentPlayData = playInfo; // TODO meta as readonly
			await AfterResourceStarted.InvokeAsync(this, playInfo);
		}

		private async Task StartCurrent(InvokerData invoker, bool manually = true)
		{
			var pli = playlistManager.Current ?? throw Error.LocalStr(strings.error_playlist_is_empty);
			try
			{
				await StartResource(invoker, pli);
			}
			catch (AudioBotException ex)
			{
				Log.Warn("Skipping: {0} because {1}", pli, ex.Message);
				await PlayNextMusic();
			}
		}

		public Task Stop() => StopInternal(false);

		private async Task StopInternal(bool songEndedByCallback)
		{
			await ResourceStopped.InvokeAsync(this, new SongEndEventArgs(songEndedByCallback));

			if (songEndedByCallback)
			{
				try
				{
					await PlayNextMusic();
					return;
				}
				catch (AudioBotException ex) { Log.Info("Song queue ended: {0}", ex.Message); }
			}
			else
			{
				PlayerConnection.Stop();
			}

			CurrentPlayData = null;
			PlaybackStopped?.Invoke(this, EventArgs.Empty);
		}

		private async Task UpdateOwnChannel(ulong channelID = 0)
		{
			if (Ts3FullClient is null)
			{
				Log.Error("[PlayManager.UpdateOwnChannel] Ts3FullClient is null");
				return;
			}
			R<WhoAmI, CommandError> who = await Ts3FullClient.WhoAmI();
			if (who.Value is null)
			{
				throw new NullReferenceException();
			}
			if (channelID < 1) channelID = who.Value.ChannelId.Value;
			ownChannelID = channelID;
			ownChannelClients.Clear();
			R<ClientList[], CommandError> r = await Ts3FullClient.ClientList();
			if (!r)
			{
				throw new Exception($"Clientlist failed ({r.Error.ErrorFormat()})");
			}
			foreach (var client in r.Value.ToList())
			{
				if (client.ChannelId.Value == channelID)
				{
					if (client.ClientId == Ts3FullClient.ClientId) continue;
					ownChannelClients.Add(client.ClientId.Value);
				}
			}
		}

		private static PlaylistItem UpdateItem(InvokerData invoker, PlaylistItem item)
		{
			item.PlayInfo ??= new PlayInfo();
			item.PlayInfo.ResourceOwnerUid = invoker.ClientUid;
			return item;
		}


		public async Task Update(SongInfoChanged newInfo)
		{
			var data = CurrentPlayData;
			if (data is null)
				return;
			if (newInfo.Title != null)
				data.ResourceData.ResourceTitle = newInfo.Title;
			// further properties...
			try
			{
				await OnResourceUpdated.InvokeAsync(this, data);
			}
			catch (AudioBotException ex)
			{
				Log.Warn(ex, "Error in OnResourceUpdated event.");
			}
		}

		private async Task SetAvatarAndDesc(MusicInfo musicInfo, string desc)
		{
			if (ts3Client is null)
			{
				Log.Error("[PlayManager.SetAvatarAndDesc] ts3Client is null");
				return;
			}
			await MainCommands.CommandBotDescriptionSet(ts3Client, desc);
			await MainCommands.CommandBotAvatarSet(ts3Client, musicInfo.Image);
		}

		public void SetPlayList(PlayListMeta meta, List<MusicInfo> list)
		{
			PlayListMeta = meta;
			MusicInfoList = new List<MusicInfo>(list);
			currentPlay = 0;
			if (Mode == Mode.RandomPlay || Mode == Mode.RandomLoopPlay)
			{
				Utils.ShuffleArrayList(MusicInfoList);
			}
		}

		public void ShufflePlayList()
		{
			Utils.ShuffleArrayList(MusicInfoList);
		}

		public async Task PlayMusic(MusicInfo musicInfo)
		{
			if (NeteaseAPI is null || Header is null)
			{
				Log.Error("[PlayManager.PlayMusic] neteaseApi is null || header is null");
				return;
			}
			string desc;
			try
			{
				await musicInfo.InitMusicInfo(NeteaseAPI, Header);
				string? musicUrl = await musicInfo.GetURL(NeteaseAPI, Header);
				Log.Info($"Music name: {musicInfo.Name}, picUrl: {musicInfo.Image}, url: {musicUrl}");
				if (musicUrl is null)
				{
					Log.Error("[PlayManager.PlayMusic] musicUrl is null");
					return;
				}


				if (musicUrl.StartsWith("error"))
				{
					if (ts3Client is null)
					{
						Log.Info("Failed to get link [{musicInfo.Name}] {musicUrl}");
					}
					else
					{
						await ts3Client.SendChannelMessage($"音乐链接获取失败 [{musicInfo.Name}] {musicUrl}");
					}
					await PlayNextMusic();
					return;
				}

				CurrentMusicInfo = musicInfo;
				await Play(Invoker, new MediaPlayResource(musicUrl, await musicInfo.GetAudioResource(), await musicInfo.GetImage(), false));

				if (ts3Client is null)
				{
					Log.Info($"Playing {musicInfo.GetFullNameBBCode()}");
				} else
				{
					await ts3Client.SendChannelMessage($"► 正在播放：{musicInfo.GetFullNameBBCode()}");
				}

				if (musicInfo.InPlayList)
				{
					desc = $"[{currentPlay}/{MusicInfoList.Count}] {musicInfo.GetFullName()}";
				}
				else
				{
					desc = musicInfo.GetFullName();
				}
			}
			catch (Exception e)
			{
				Log.Error(e, "PlayMusic error" + e.ToString());
				if (ts3Client != null)
				{
					await ts3Client.SendChannelMessage($"播放音乐失败 [{musicInfo.Name}]");
				}
				await PlayNextMusic();
				return;
			}
			CurrentMusicInfo = musicInfo;

			await MainCommands.CommandBotDescriptionSet(ts3Client, desc);
			await MainCommands.CommandBotAvatarSet(ts3Client, musicInfo.Image);
		}

		public async Task PlayNextMusic()
		{
			if (MusicInfoList.Count == 0)
			{
				if (ts3Client is null)
				{
					Log.Error("[PlayManager.PlayNextMusic] ts3Client is null");
					throw new NullReferenceException("ts3Client is null");
				}
				await ts3Client.SendChannelMessage("歌单为空");
				return;
			}
			MusicInfo musicInfo = GetNextMusic();
			CurrentMusicInfo = musicInfo;
			await Play(Invoker, await GetPlayObject(musicInfo));
		}

		public void ChangeCookies(string cookies, bool isQrlogin)
		{
			if (Header is null || yunConf is null)
			{
				Log.Error("[PlayManager.ChangeCookies] header is null || yunConf is null");
				return;
			}
			Console.WriteLine(cookies);
			var cookie = Utils.ProcessCookie(cookies);
			Header["Cookie"] = cookie;
			yunConf.Header["Cookie"] = cookie;
			yunConf.isQrlogin = isQrlogin;
			yunConf.Save();
		}

		public string changeMode(int mode)
		{
			if (mode == -1)
			{
				return "播放模式选择【0=顺序播放 1=顺序循环 2=随机 3=随机循环";
			}
			if (Enum.IsDefined(typeof(Mode), mode))
			{
				this.Mode = (Mode)mode;
				if (yunConf is null) return "yunConf is null";
				yunConf.playMode = this.Mode;
				yunConf.Save();

				return (this.Mode switch
				{
					Mode.SeqPlay => "当前播放模式为顺序播放",
					Mode.SeqLoopPlay => "当前播放模式为顺序循环",
					Mode.RandomPlay => "当前播放模式为随机播放",
					Mode.RandomLoopPlay => "当前播放模式为随机循环",
					_ => "请输入正确的播放模式",
				});
			}
			else
			{
				return "请输入正确的播放模式";
			}
		}

		public async Task<Status1> CheckLoginStatus(string key)
		{
			return await Utils.HttpGetAsync<Status1>($"{this.NeteaseAPI}/login/qr/check?key={key}&timestamp={Utils.GetTimeStamp()}");
		}

		public void ClearSongList()
		{
			MusicInfoList.Clear();
		}

		public async Task<List<MusicInfo>?> GetGedanMusicInfoList(GedanDetail? gedanDetail)
		{
			if (Header is null || ts3Client is null) throw new NullReferenceException();
			if (gedanDetail is null) throw new ArgumentNullException(nameof(gedanDetail));

			int? trackCount = gedanDetail.playlist.trackCount;
			if (trackCount == 0)
			{
				return null;
			}
			GeDan? Gedans = await Utils.HttpGetAsync<GeDan>($"{NeteaseAPI}/playlist/track/all?id={gedanDetail.playlist.id}", Header);
			long? numOfSongs = Gedans.songs.Count();
			if (numOfSongs > 100)
			{
				await ts3Client.SendChannelMessage($"警告：歌单过大，可能需要一定的时间生成 [{numOfSongs}]");
			}
			List<MusicInfo> gedanMusicInfoList = new List<MusicInfo>();
			for (int i = 0; i < numOfSongs; i++)
			{
				long? musicid = Gedans.songs[i].id;
				if (musicid > 0)
				{
					gedanMusicInfoList.Add(new MusicInfo(musicid.ToString()));
				}
			}
			return gedanMusicInfoList;
		}

		public async Task<string> GedanAdd(string idOrName)
		{
			if (ts3Client is null) throw new NullReferenceException(idOrName);
			
			GedanDetail? gedanDetail = await GetGedanDetail(idOrName);

			if (gedanDetail is null) return "添加失败, 无法获取歌单";
			await ts3Client.SendChannelMessage("正在进行");

			List<MusicInfo>? gedanMusicInfoList = await GetGedanMusicInfoList(gedanDetail.playlist.id);
			if (gedanMusicInfoList is null) return "无法获取歌单歌曲列表";

			foreach (MusicInfo musicInfo in gedanMusicInfoList)
			{
				try
				{
					AddMusic(musicInfo);
				}
				catch (Exception e)
				{
					Log.Error(e);
					await ts3Client.SendChannelMessage($"[PlayManager.GedanAdd] {e.Message}");
				}
			}

			return "歌单添加完毕：" + gedanDetail.playlist.name + " [" + gedanMusicInfoList.Count.ToString() + "]";
		}

		public async Task<string> GedanSet(string idOrName)
		{
			MusicInfoList.Clear();
			return (await GedanAdd(idOrName)).Replace("添加", "设置");
		}

		public async Task<List<MusicInfo>?> GetGedanMusicInfoList(string? idOrName)
		{
			GedanDetail? gedanDetail = await GetGedanDetail(idOrName);
			if (gedanDetail is null)
			{
				Log.Error("[PlayManager.GenListGedan] gedanDetail is null");
				return null;
			}
			return await GetGedanMusicInfoList(gedanDetail);
		}

		public async Task<List<MusicInfo>?> GetGedanMusicInfoList(long? id)
		{
			GedanDetail? gedanDetail = await GetGedanDetail(id.ToString());
			if (gedanDetail is null)
			{
				Log.Error("[PlayManager.GenListGedan] gedanDetail is null");
				return null;
			}
			return await GetGedanMusicInfoList(gedanDetail);
		}

		public async Task<string> GetLoginKey()
		{
			LoginKey? loginKey = await Utils.HttpGetAsync<LoginKey>($"{this.NeteaseAPI}/login/qr/key?timestamp={Utils.GetTimeStamp()}");
			return loginKey.data.unikey;
		}

		public async Task<string> GetLoginQRImage(string key)
		{
			LoginImg? loginImg = await Utils.HttpGetAsync<LoginImg>($"{this.NeteaseAPI}/login/qr/create?key={key}&qrimg=true&timestamp={Utils.GetTimeStamp()}");
			return loginImg.data.qrimg;
		}

		public static async Task<RespStatus> GetLoginStatusAasync(string server, Dictionary<string, string> header)
		{
			return await Utils.HttpGetAsync<RespStatus>($"{server}/login/status?timestamp={Utils.GetTimeStamp()}", header);
		}

		private MusicInfo GetNextMusic()
		{
			MusicInfo? result = MusicInfoList[0];
			MusicInfoList.RemoveAt(0);
			if (Mode == Mode.SeqLoopPlay || Mode == Mode.RandomLoopPlay) // 循环的重新加入列表
			{
				MusicInfoList.Add(result);
				currentPlay += 1;
			}
			else
			{
				currentPlay = 1; // 不是循环播放就固定当前播放第一首
			}

			if (Mode == Mode.RandomLoopPlay)
			{
				if (currentPlay >= MusicInfoList.Count)
				{
					Utils.ShuffleArrayList(MusicInfoList);
					currentPlay = 1; // 重排了就从头开始
				}
			}

			return result;
		}

		public List<MusicInfo> GetNextPlayList(int limit = 3)
		{
			var list = new List<MusicInfo>();
			limit = Math.Min(limit, MusicInfoList.Count);
			for (int i = 0; i < limit; i++)
			{
				list.Add(MusicInfoList[i]);
			}
			for (int i = limit; i < MusicInfoList.Count; i++)
			{

			}
			return list;
		}

		public async Task<GedanDetail?> GetGedanDetail(string? idOrName)
		{
			if (string.IsNullOrEmpty(idOrName)) return null;
			if (Header is null || ts3Client is null) throw new NullReferenceException();
			string listId = Utils.ExtractIdFromAddress(idOrName);
			if (!Utils.IsNumber(listId))
			{
				string urlSearch = $"{NeteaseAPI}/search?keywords={idOrName}&limit=1&type=1000";
				SearchGedan searchgedan = await Utils.HttpGetAsync<SearchGedan>(urlSearch);
				if (searchgedan.result.playlists.Length == 0)
				{
					await ts3Client.SendChannelMessage("无法找到歌单");
					return null;
				}
				listId = searchgedan.result.playlists[0].id.ToString();
			}
			return await Utils.HttpGetAsync<GedanDetail>($"{this.NeteaseAPI}/playlist/detail?id={listId}&timestamp={Utils.GetTimeStamp()}", Header);
		}

		public async Task<string> GetPlayListString(int limit = 3)
		{
			var nextPlayList = GetNextPlayList(limit);
			var descBuilder = new StringBuilder();
			if (CurrentMusicInfo != null)
			{
				descBuilder.AppendLine($"\n当前正在播放：{CurrentMusicInfo.GetFullNameBBCode()}");
			}
			else
			{
				descBuilder.AppendLine($"\n当前正在播放：literally 空气");
			}
			var modeStr = Mode switch
			{
				Mode.SeqPlay => "顺序播放",
				Mode.SeqLoopPlay => "当顺序循环",
				Mode.RandomPlay => "随机播放",
				Mode.RandomLoopPlay => "随机循环",
				_ => $"未知模式{Mode}",
			};
			descBuilder.AppendLine($"当前播放模式：{modeStr}");
			if (nextPlayList.Count == 0)
			{
				descBuilder.Append("当前播放列表为空");
				return descBuilder.ToString();
			}
			descBuilder.Append("播放列表 ");
			if (PlayListMeta != null)
			{
				descBuilder.Append($"[URL=https://music.163.com/#/playlist?id={PlayListMeta.NCMId}]{PlayListMeta.Name}[/URL] ");
			}
			descBuilder.AppendLine($"[{currentPlay}/{MusicInfoList.Count}]");

			for (var i = 0; i < nextPlayList.Count; i++)
			{
				var music = nextPlayList[i];
				if (music.DirectUrl != null)
				{
					descBuilder.AppendLine($"{i + 1}: {music.GetURLFileName()}");
				} else
				{
					await music.InitMusicInfo(NeteaseAPI, Header);
					descBuilder.AppendLine($"{i + 1}: {music.GetFullNameBBCode()}");
				}
			}

			return descBuilder.ToString();
		}

		public async Task<MediaPlayResource?> GetPlayObject(MusicInfo musicInfo)
		{
			if (ts3Client is null)
			{
				Log.Error("[PlayManager.GetPlayObject] ts3Client");
				return null;
			}

			if (this.NeteaseAPI is null || Header is null)
			{
				Log.Error("[PlayManager.GetPlayObject] this.neteaseApi is null || header is null");
				return null;
			}
			try
			{
				await musicInfo.InitMusicInfo(NeteaseAPI, Header);
				string? url = await musicInfo.GetURL(NeteaseAPI, Header);
				if (url is null || url.StartsWith("error"))
				{
					string? which = musicInfo.DirectUrl is null ? musicInfo.Name : musicInfo.DirectUrl;
					which ??= "null";
					await ts3Client.SendChannelMessage($"音乐链接获取失败或目录无效 [{which}] {url}");
					await PlayNextMusic();
					return null;
				}
				Log.Info($"Music name: {musicInfo.GetFullName()}, picUrl: {musicInfo.GetImage()}, url: {url}");

				await ts3Client.SendChannelMessage($"► 正在播放：{musicInfo.GetFullNameBBCode()}");

				string? desc;
				if (musicInfo.InPlayList)
				{
					desc = $"[{currentPlay}/{MusicInfoList.Count}] {musicInfo.GetFullName()}";
				}
				else
				{
					desc = musicInfo.GetFullName();
				}
				await SetAvatarAndDesc(musicInfo, desc);

				return new MediaPlayResource(url, await musicInfo.GetAudioResource(), await musicInfo.GetImage(), false);
			}
			catch (Exception e)
			{
				Log.Error(e, "PlayMusic error" + e.ToString());
				await ts3Client.SendChannelMessage($"播放音乐失败 [{musicInfo.Name}]");
				await PlayNextMusic();
				return null;
			}
		}

		public async Task<string> LoginSms(string phoneandcode)
		{
			var phoneAndCode = phoneandcode.Split(' ');
			string phone = phoneAndCode[0];
			string code;
			if (phoneAndCode.Length == 2)
			{
				code = phoneAndCode[1];
			}
			else
			{
				code = "";
			}

			if (!string.IsNullOrEmpty(code) && code.Length != 4)
			{
				return "请输入正确的验证码";
			}
			string url;
			Status1 status;
			if (string.IsNullOrEmpty(code))
			{
				url = $"{NeteaseAPI}/captcha/sent?phone={phone}&t={Utils.GetTimeStamp()}";
				status = await Utils.HttpGetAsync<Status1>(url);
				if (status.code == 200)
				{
					return "验证码已发送";
				}
				else
				{
					return "发送失败";
				}
			}

			url = $"{NeteaseAPI}/captcha/verify?phone={phone}&captcha={code}&t={Utils.GetTimeStamp()}";
			status = await Utils.HttpGetAsync<Status1>(url);
			if (status.code != 200)
			{
				return "验证码错误";
			}
			url = $"{NeteaseAPI}/login/cellphone?phone={phone}&captcha={code}&t={Utils.GetTimeStamp()}";
			status = await Utils.HttpGetAsync<Status1>(url);
			if (status.code == 200)
			{
				ChangeCookies(status.cookie, false);
				return "登陆成功";
			}
			else
			{
				return "登陆失败";
			}
		}

		public async Task<string> LoginQR()
		{
			if (ts3Client is null) return "ts3Client is null";
			string key = await GetLoginKey();
			string qrimg = await GetLoginQRImage(key);

			await ts3Client.SendChannelMessage("正在生成二维码");
			await ts3Client.SendChannelMessage(qrimg);
			Log.Debug(qrimg);
			string[] img = qrimg.Split(",");
			byte[] bytes = Convert.FromBase64String(img[1]);
			Stream stream = new MemoryStream(bytes);
			try
			{
				await ts3Client.UploadAvatar(stream);
				await MainCommands.CommandBotDescriptionSet(ts3Client, "请用网易云APP扫描二维码登陆");
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.ToString());
			}

			int i = 0;
			long? code;
			string? result;
			string? cookies;
			while (true)
			{
				Status1 status = await CheckLoginStatus(key);
				code = status.code;
				cookies = status.cookie;
				i++;
				Thread.Sleep(1000);
				if (i == 120)
				{
					result = "登陆失败或者超时";
					await ts3Client.SendChannelMessage("登陆失败或者超时");
					break;
				}
				if (code == 803)
				{
					result = "登陆成功";
					await ts3Client.SendChannelMessage("登陆成功");
					break;
				}
			}
			try
			{
				await ts3Client.DeleteAvatar();
				await ts3Client.ChangeDescription("已登陆");
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
			}
			ChangeCookies(cookies, true);

			return result;
		}

		public string Switch(int from, int to)
		{
			try
			{
				from--;
				to--;
				MusicInfo tmp = MusicInfoList[to];
				MusicInfoList[to] = MusicInfoList[from];
				MusicInfoList[from] = tmp;
			}
			catch (Exception  e) {
				return e.Message;
			}
			return "交换成功";
		}
	}
}
