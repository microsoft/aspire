// @ts-check

import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig({
  files: ['apphost.ts'],
  extends: [tseslint.configs.base],
  languageOptions: {
    parserOptions: {
      projectService: true,
    },
  },
  rules: {
    '@typescript-eslint/no-floating-promises': ['error', { checkThenables: true }],
  },
});
