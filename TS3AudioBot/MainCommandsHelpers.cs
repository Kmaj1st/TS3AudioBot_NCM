using CliWrap;
using System.Threading.Tasks;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.CommandSystem.CommandResults;
// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

internal static class MainCommandsHelpers
{

	[Command("play")]
	public static async Task CommandPlay(PlayManager playManager, Player playerConnection, InvokerData invoker)
	{
		if (!playManager.IsPlaying)
			await playManager.Play(invoker);
		else
			playerConnection.Paused = false;
	}

	[Command("play")]
	public static async Task CommandPlay(PlayManager playManager, InvokerData invoker, string url, params string[] attributes)
		=> await playManager.Play(invoker, url, meta: PlayManager.ParseAttributes(attributes));

	[Command("play")]
	public static async Task CommandPlay(PlayManager playManager, InvokerData invoker, IAudioResourceResult rsc, params string[] attributes)
		=> await playManager.Play(invoker, rsc.AudioResource, meta: PlayManager.ParseAttributes(attributes));
}
