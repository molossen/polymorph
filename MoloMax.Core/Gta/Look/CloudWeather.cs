namespace MoloMax.Core.Gta.Look;


// The game's weathers (names of its weather files).
public enum Weather
{
    ExtraSunny,
    Clear,
    Clouds,
    Clearing,
    Overcast,
    Smog,
    Foggy,
    Rain,
    Thunder,
    Snow,
}

// The sky of a weather: the cloud layer (Coverage 0 to 1 of the sky, Density: how thick their inside is, Base and
// Thickness in metres, Heaps 0 flat layers to 1 tall heaps, Dark: rain clouds absorb more), how much of the sun reaches
// the ground (Sun), fog distance factor (Fog), sky turned grey (Grey), the wind (metres per second) and how wet the
// ground is (Wet, 0 to 1) and how much snow lies on it (Snow, 0 to 1).
public readonly record struct CloudWeather(float Coverage, float Density, float Base, float Thickness, float Heaps, float Dark,
    float Sun, float Fog, float Grey, float Wind, float Wet = 0f, float Snow = 0f)
{
    public static CloudWeather For(Weather weather) => weather switch
    {
        Weather.ExtraSunny => new(0.22f, 0.8f, 1600f, 900f, 0.8f, 0f, 1f, 1f, 0f, 6f),
        Weather.Clear => new(0.38f, 1f, 1500f, 1300f, 0.9f, 0f, 1f, 1f, 0f, 8f),
        Weather.Clouds => new(0.48f, 1f, 1400f, 1800f, 1f, 0.05f, 0.85f, 0.9f, 0.15f, 10f),
        Weather.Clearing => new(0.56f, 1.1f, 1200f, 1700f, 0.8f, 0.2f, 0.75f, 0.85f, 0.25f, 14f, 0.35f),
        Weather.Overcast => new(0.86f, 1.1f, 1100f, 1400f, 0.35f, 0.3f, 0.45f, 0.7f, 0.6f, 9f),
        Weather.Smog => new(0.42f, 0.8f, 1700f, 1000f, 0.6f, 0.1f, 0.8f, 0.45f, 0.35f, 4f),
        Weather.Foggy => new(0.5f, 0.9f, 1300f, 1100f, 0.4f, 0.15f, 0.65f, 0.18f, 0.7f, 3f),
        Weather.Rain => new(0.93f, 1.3f, 900f, 1800f, 0.5f, 0.6f, 0.3f, 0.5f, 0.8f, 14f, 1f),
        Weather.Thunder => new(0.98f, 1.6f, 800f, 2500f, 0.7f, 0.8f, 0.18f, 0.45f, 0.9f, 18f, 1f),
        Weather.Snow => new(0.9f, 1.1f, 900f, 1500f, 0.35f, 0.25f, 0.4f, 0.5f, 0.8f, 6f, 0f, 1f),
        _ => new(0.38f, 1f, 1500f, 1300f, 0.9f, 0f, 1f, 1f, 0f, 8f),
    };
}
