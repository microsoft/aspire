import { useEffect, useState } from "react";
import {
  weatherHeading,
  workspaceDescription,
  workspaceName,
} from "@aspirejavascript/pnpm-workspace-shared";
import "./App.css";

type Forecast = {
  date: string;
  temperatureC: number;
  temperatureF: number;
  summary: string;
};

function App() {
  const [forecasts, setForecasts] = useState<Array<Forecast>>([]);

  useEffect(() => {
    const fetchForecasts = async () => {
      const response = await fetch("/api/weatherforecast");
      const data: Array<Forecast> = await response.json();
      setForecasts(data);
    };

    void fetchForecasts();
  }, []);

  return (
    <main className="app-shell">
      <section className="hero">
        <span className="eyebrow">Aspire JavaScript hosting</span>
        <h1>{weatherHeading}</h1>
        <p>{workspaceDescription}</p>
        <p>
          Shared with <strong>{workspaceName}</strong> package references.
        </p>
      </section>

      <section className="card">
        <h2>Forecasts</h2>
        <table>
          <thead>
            <tr>
              <th>Date</th>
              <th>Temp. (C)</th>
              <th>Temp. (F)</th>
              <th>Summary</th>
            </tr>
          </thead>
          <tbody>
            {(forecasts.length > 0
              ? forecasts
              : [
                  {
                    date: "Waiting for forecast data",
                    temperatureC: 0,
                    temperatureF: 0,
                    summary: "No forecasts loaded yet",
                  },
                ]).map((forecast) => (
              <tr key={forecast.date}>
                <td>{forecast.date}</td>
                <td>{forecast.temperatureC}</td>
                <td>{forecast.temperatureF}</td>
                <td>{forecast.summary}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </main>
  );
}

export default App;