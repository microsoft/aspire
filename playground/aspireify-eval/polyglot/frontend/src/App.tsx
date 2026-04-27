import { useState, useEffect } from 'react';

// These would be injected by Aspire after aspirification
const WEATHER_API = import.meta.env.VITE_WEATHER_API_URL || 'http://localhost:8001';
const GEO_API = import.meta.env.VITE_GEO_API_URL || 'http://localhost:8002';
const EVENTS_API = import.meta.env.VITE_EVENTS_API_URL || 'http://localhost:8003';

export default function App() {
  const [city, setCity] = useState('seattle');
  const [weather, setWeather] = useState<any>(null);
  const [geo, setGeo] = useState<any>(null);
  const [events, setEvents] = useState<any[]>([]);

  useEffect(() => {
    fetch(`${WEATHER_API}/weather/${city}`).then(r => r.json()).then(setWeather).catch(() => {});
    fetch(`${GEO_API}/geocode/${city}`).then(r => r.json()).then(setGeo).catch(() => {});
    fetch(`${EVENTS_API}/events/${city}`).then(r => r.json()).then(setEvents).catch(() => {});
  }, [city]);

  return (
    <div>
      <h1>CityServices — {city}</h1>
      <select value={city} onChange={e => setCity(e.target.value)}>
        {['seattle', 'new-york', 'san-francisco', 'chicago', 'austin'].map(c => (
          <option key={c} value={c}>{c}</option>
        ))}
      </select>
      <h2>Weather</h2>
      <pre>{JSON.stringify(weather, null, 2)}</pre>
      <h2>Location</h2>
      <pre>{JSON.stringify(geo, null, 2)}</pre>
      <h2>Events</h2>
      <pre>{JSON.stringify(events, null, 2)}</pre>
    </div>
  );
}
