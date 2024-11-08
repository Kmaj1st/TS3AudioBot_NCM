using System;
using System.IO;
using YamlDotNet.Serialization;

namespace TS3AudioBot
{
	public class YamlSerialize
    {
        static public void Serializer<T>(T obj, string path)
        {
			StreamWriter yamlWriter = File.CreateText(path);
			var yamlSerializer = new Serializer();
            yamlSerializer.Serialize(yamlWriter, obj);
            yamlWriter.Close();
        }
		
        static public T Deserializer<T>(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException();
            }
            StreamReader yamlReader = File.OpenText(path);
            var yamlDeserializer = new Deserializer();

			try
			{
	            T obj = yamlDeserializer.Deserialize<T>(yamlReader);
				yamlReader.Close();
				return obj;
			} catch (Exception e)
			{
				NLog.LogManager.GetCurrentClassLogger().Error(e);
				throw new Exception();
			}
        }
    }
}
