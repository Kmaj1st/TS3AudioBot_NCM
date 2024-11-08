using System;
using System.Collections.Generic;
using System.Text;

namespace TS3AudioBot
{
	public class YamlPlayList
	{
		public Mode Mode { get; private set; }
		public List<MusicInfo> MusicInfoList { get; private set; }
		public PlayListMeta? PlayListMeta { get; private set; }

		public YamlPlayList (
				Mode Mode,
				List<MusicInfo> MusicInfoList,
				PlayListMeta? PlayListMeta
		) {
			this.Mode = Mode;
			this.MusicInfoList = MusicInfoList;
			this.PlayListMeta = PlayListMeta;
		}

		public YamlPlayList() {
			this.Mode = Mode.RandomLoop;
			this.MusicInfoList = new List<MusicInfo>();
		}
	}
}
