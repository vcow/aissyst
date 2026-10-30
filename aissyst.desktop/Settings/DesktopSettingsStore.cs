using System.Text.Encodings.Web;
using System.Text.Json;

namespace aissyst.desktop.Settings;

internal sealed class DesktopSettingsStore
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	private readonly SemaphoreSlim _save = new(1, 1);
	public string FilePath { get; }
	public DesktopApplicationSettings Active { get; }
	public DesktopApplicationSettings Saved { get; private set; }
	public Exception? LoadError { get; }

	public DesktopSettingsStore(string? filePath = null)
	{
		FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"aissyst", "desktop-settings.json");
		try
		{
			Active = File.Exists(FilePath)
				? (JsonSerializer.Deserialize<DesktopApplicationSettings>(File.ReadAllText(FilePath), JsonOptions)
					?? throw new InvalidDataException("The settings file contains no settings.")).Validated()
				: new DesktopApplicationSettings().Validated();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
		{
			LoadError = exception;
			Active = new DesktopApplicationSettings().Validated();
		}
		Saved = Active;
	}

	public async Task SaveAsync(DesktopApplicationSettings settings)
	{
		var validated = settings.Validated();
		await _save.WaitAsync();
		var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
			await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(validated, JsonOptions));
			File.Move(temporaryPath, FilePath, overwrite: true);
			Saved = validated;
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
			_save.Release();
		}
	}
}
