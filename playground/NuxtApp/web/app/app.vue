<script setup lang="ts">
const { data, error } = await useFetch<{ message: string }>('/api/message');
</script>

<template>
    <main>
        <h1>Nuxt hosted by Aspire</h1>
        <p v-if="error">Backend request failed: {{ error.message }}</p>
        <p v-else>{{ data?.message }}</p>
    </main>
</template>
