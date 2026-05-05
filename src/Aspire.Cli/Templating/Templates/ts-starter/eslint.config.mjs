// @ts-check

import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig({
  files: ['apphost.ts'],
  extends: [tseslint.configs.base],
  languageOptions: {
    parserOptions: {
      project: './tsconfig.apphost.json',
      tsconfigRootDir: import.meta.dirname,
    },
  },
  rules: {
    '@typescript-eslint/no-floating-promises': ['error', { checkThenables: true }],
  },
});
