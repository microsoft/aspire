//@ts-check

'use strict';

const path = require('path');
const webpack = require('webpack');

/**
 * `src/testing/e2eStateFileBridge.ts` is a test control channel: it registers a wildcard debug
 * adapter tracker, mirrors extension state to a file, and executes commands read from a file path
 * supplied in an environment variable. `extension.ts` imports it unconditionally, so without this
 * replacement the whole channel is bundled into the published extension and gated only by a runtime
 * environment variable - which anyone able to set environment variables on the VS Code process can
 * satisfy.
 *
 * Production is the mode `vscode:prepublish` builds in (`yarn package`), and therefore the mode
 * every shipped VSIX is built in, so swapping in a no-op module there keeps the bridge out of the
 * artifact entirely. Development and E2E bundles build with `mode: 'none'` and keep the real
 * implementation, which is what the E2E runner drives.
 */
const e2eBridgeRequestPattern = /[\\/]testing[\\/]e2eStateFileBridge$/;
const e2eBridgeProductionStub = path.resolve(__dirname, 'src', 'testing', 'e2eStateFileBridge.production.ts');

//@ts-check
/** @typedef {import('webpack').Configuration} WebpackConfig **/

/** @type WebpackConfig */
const extensionConfig = {
  target: 'node', // VS Code extensions run in a Node.js-context 📖 -> https://webpack.js.org/configuration/node/
	mode: 'none', // this leaves the source code as close as possible to the original (when packaging we set this to 'production')

  entry: './src/extension.ts', // the entry point of this extension, 📖 -> https://webpack.js.org/configuration/entry-context/
  output: {
    // the bundle is stored in the 'dist' folder (check package.json), 📖 -> https://webpack.js.org/configuration/output/
    path: path.resolve(__dirname, 'dist'),
    filename: 'extension.js',
    libraryTarget: 'commonjs2'
  },
  externals: {
    vscode: 'commonjs vscode' // the vscode-module is created on-the-fly and must be excluded. Add other modules that cannot be webpack'ed, 📖 -> https://webpack.js.org/configuration/externals/
    // modules added here also need to be added in the .vscodeignore file
  },
  resolve: {
    // support reading TypeScript and JavaScript files, 📖 -> https://github.com/TypeStrong/ts-loader
    extensions: ['.ts', '.js']
  },
  module: {
    rules: [
      {
				test: /\.ts$/,
				exclude: /node_modules/,
				use: [
					{
						loader: 'ts-loader',
					},
				],
			},
      {
        test: /\.wasm$/,
        type: 'asset/resource'
      },
    ]
  },
  devtool: 'source-map',
  infrastructureLogging: {
    level: "log", // enables logging required for problem matchers
  },
};
/**
 * Exported as a function so the build can react to `--mode`. webpack calls this with the parsed CLI
 * arguments, so `argv.mode` is 'production' for `yarn package` and undefined for `yarn compile`.
 */
module.exports = (_env, argv) => {
  // A fresh array per call so repeated invocations cannot accumulate plugins on the shared config.
  const plugins = argv && argv.mode === 'production'
    ? [new webpack.NormalModuleReplacementPlugin(e2eBridgeRequestPattern, e2eBridgeProductionStub)]
    : [];

  return [ { ...extensionConfig, plugins } ];
};

module.exports.e2eBridgeRequestPattern = e2eBridgeRequestPattern;
module.exports.e2eBridgeProductionStub = e2eBridgeProductionStub;
