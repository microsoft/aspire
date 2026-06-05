import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { httpResource } from '@angular/common/http';
import { WeatherForecasts } from '../types/weatherForecast';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent {
  protected readonly title = 'weather';
  protected readonly forecasts = httpResource<WeatherForecasts>(
    () => 'api/weatherforecast',
    { defaultValue: [] },
  );
}
