using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TS3AudioBot;
using TS3AudioBot.Helper;

public static class Utils
{
	public static async Task<bool> CheckUrlResponse(string? url)
	{
		if (String.IsNullOrWhiteSpace(url)) return false;
		using var client = new HttpClient();
		try
		{
			HttpResponseMessage response = await client.GetAsync(url);
			return response.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	public static string? ConvertToPlainUrl(string? arg)
	{
		if (String.IsNullOrWhiteSpace(arg)) return null;
		string pattern = @"(\[URL\])?([^\s.]+\.[\w][^\s]+)(\[\/URL\])";
		try
		{
			MatchCollection matches = Regex.Matches(arg, pattern);
			return matches[0].Groups[2].Value;

		}
		catch
		{
			return arg;
		}
	}

    static readonly string[] CookieBlackList = { "expires", "path", "domain", "max-age", "secure", "httponly", "samesite", "" };

	public static Dictionary<string, string> CookieToDict(string cookie)
    {
        Dictionary<string, string> dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(cookie))
        {
            return dict;
        }
        string[] cookies = cookie.Split(';');
        foreach (var item in cookies)
        {
            var items = item.Trim().Split('=');
            var key = items[0].Trim();
            var value = items.Length == 2 ? items[1].Trim() : "";
            if (CookieBlackList.Contains(key.ToLower()))
            {
                continue;
            }

            dict[key] = value;
        }
        return dict;
    }

    public static string ExtractIdFromAddress(string address)
    {
        string pattern = @"id=(\d+)";
        Match match = Regex.Match(address, pattern);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        else
        {
            return address;
        }
    }

	public static string GetTimeStamp()
    {
        TimeSpan ts = DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, 0);
        return Convert.ToInt64(ts.TotalSeconds).ToString();
    }

	public static async Task<T> HttpGetAsync<T>(string url, Dictionary<string, string> header = null)
    {
        var request = WebWrapper.Request(url);
        
        if (header != null)
        {
            foreach (var item in header)
            {
                request.WithHeader(item.Key, item.Value);
            }
        }

        return await request.AsJson<T>();
    }

    public static string MergeCookie(string cookie, string new_cookie)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return new_cookie;
        }
        if (string.IsNullOrEmpty(new_cookie))
        {
            return cookie;
        }
        Dictionary<string, string> dict = CookieToDict(cookie);
        Dictionary<string, string> new_dict = CookieToDict(new_cookie);
        foreach (var item in new_dict)
        {
            dict[item.Key] = item.Value;
        }
        return string.Join("; ", dict.Select(x => $"{x.Key}={x.Value}"));
    }

    public static bool IsNumber(string strNumber)
    {
        Regex regex = new Regex(@"^\d+$");
        return regex.IsMatch(strNumber);
    }

	public static async Task<bool> IsValidPathOrFile(string? pathOrFile)
	{
		if (String.IsNullOrWhiteSpace(pathOrFile)) return false;
		pathOrFile = Utils.ConvertToPlainUrl(pathOrFile);
		return ((File.Exists(pathOrFile) || await Utils.CheckUrlResponse(pathOrFile)));
	}

    public static string ProcessCookie(string cookie)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return "";
        }
        var cookies = CookieToDict(cookie);
        return string.Join("; ", cookies.Select(x => $"{x.Key}={x.Value}"));
    }
    public static void ShuffleArrayList<T>(List<T> arrayList)
    {
        Random random = new Random();

        int n = arrayList.Count;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            var value = arrayList[k];
            arrayList[k] = arrayList[n];
            arrayList[n] = value;
        }
    }
}
