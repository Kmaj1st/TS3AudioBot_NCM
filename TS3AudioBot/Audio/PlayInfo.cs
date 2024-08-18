// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Diagnostics.CodeAnalysis;
using TSLib;

namespace TS3AudioBot.Audio
{
	public sealed class PlayInfo
	{
		public enum Source
		{
			LOC,
			URL,
			NCM,
		}

		/// <summary>Defaults to: invoker.Uid - Can be set if the owner of a song differs from the invoker.</summary>
		public Uid? ResourceOwnerUid { get; set; }
		/// <summary>Starts the song at the specified time if set.</summary>
		public TimeSpan? StartOffset { get; set; }
		/// <summary>Determines where to play a song
		public Enum? AudioSource { get; set; }

		public static Enum? ToAudioSource(string? str = null)
		{
			if (str is null) return null;
			str = str.ToLower().Replace("#", "");
			if (str.StartsWith("l") || str.StartsWith("b")) return Source.LOC;
			if (str.StartsWith("w") || str.StartsWith("n")) return Source.NCM;
			if (str.StartsWith("u")) return Source.URL;
			return null;
		}

		public static PlayInfo ToPlayInfo(string? audioSource = null)
		{
			return new PlayInfo(null, audioSource);
		}

		public override string ToString()
		{
			return $"{ResourceOwnerUid}, {this.StartOffset}, {AudioSource}";
		}

		public PlayInfo(TimeSpan? startOffset = null, string? audioSource = null)
		{
			StartOffset = startOffset;
			AudioSource = ToAudioSource(audioSource);
		}

		public PlayInfo Merge(PlayInfo other) => Merge(this, other);

		[return: NotNullIfNotNull("self")]
		[return: NotNullIfNotNull("other")]
		public static PlayInfo? Merge(PlayInfo? self, PlayInfo? other)
		{
			if (other is null)
				return self;
			if (self is null)
				return other;
			self.ResourceOwnerUid ??= other.ResourceOwnerUid;
			self.StartOffset ??= other.StartOffset;
			self.AudioSource ??= other.AudioSource;
			return self;
		}

		public static PlayInfo MergeDefault(PlayInfo? self, PlayInfo? other)
			=> Merge(self, other) ?? new PlayInfo();
	}

	public interface IMetaContainer
	{
		public PlayInfo? PlayInfo { get; set; }
	}

	public static class MetaContainerExtensions
	{
		public static T MergeMeta<T>(this T container, PlayInfo? other) where T : IMetaContainer
		{
			container.PlayInfo = PlayInfo.Merge(container.PlayInfo, other);
			return container;
		}
	}
}
