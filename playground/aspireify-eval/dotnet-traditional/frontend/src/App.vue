<template>
  <div>
    <h1>BoardApp</h1>
    <p>Status: {{ status }}</p>
    <h2>Items</h2>
    <ul>
      <li v-for="item in items" :key="item.id">
        {{ item.title }} — {{ item.isComplete ? '✅' : '⏳' }}
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';

const status = ref('loading...');
const items = ref<{ id: number; title: string; isComplete: boolean }[]>([]);

onMounted(async () => {
  try {
    const healthRes = await fetch('/api/health');
    const health = await healthRes.json();
    status.value = health.status;

    const itemsRes = await fetch('/api/items');
    items.value = await itemsRes.json();
  } catch (e) {
    status.value = 'error: ' + (e as Error).message;
  }
});
</script>
