<script lang="ts">
interface WeatherForecast {
  date: string
  temperatureC: number
  temperatureF: number
  summary: string
};

export default {
  name: 'TheWelcome',
  data() {
    return {
      forecasts: [] as WeatherForecast[],
      loading: true,
      error: null
    }
  },
  mounted() {
    fetch('api/weatherforecast')
      .then(response => response.json())
      .then(data => {
        this.forecasts = data
      })
      .catch(error => {
        this.error = error
      })
      .finally(() => (this.loading = false))
  }
}
</script>

<template>
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
      <tr v-for="forecast in forecasts" :key="forecast.date">
        <td>{{ forecast.date }}</td>
        <td>{{ forecast.temperatureC }}</td>
        <td>{{ forecast.temperatureF }}</td>
        <td>{{ forecast.summary }}</td>
      </tr>
    </tbody>
  </table>
</template>

<style>
table {
  border: none;
  border-collapse: collapse;
}

th {
  font-size: x-large;
  font-weight: bold;
  border-bottom: solid .2rem hsla(160, 100%, 37%, 1);
}

th,
td {
  padding: 1rem;
}

td {
  text-align: center;
  font-size: large;
}

tr:nth-child(even) {
  background-color: var(--vt-c-black-soft);
}
</style>