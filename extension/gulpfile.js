const { execFileSync } = require('child_process');
const gulp = require('gulp');
const path = require('path');
const fs = require('fs');
const { getL10nXlf, getL10nFilesFromXlf } = require('@vscode/l10n-dev');

/*
Language Set Docs:
https://ceapex.visualstudio.com/CEINTL/_git/SoftwareLocalization?path=/src/OneBranchPackages/Localization.Languages/Localization.Languages.props&version=GBmain&line=43&lineEnd=44&lineStartColumn=1&lineEndColumn=93&lineStyle=plain&_a=contents
<!--Visual Studio languages set -->
<VS_Main_Languages>cs;de;es;fr;it;ja;ko;pl;pt-BR;ru;tr;zh-Hans;zh-Hant</VS_Main_Languages>
*/
const languages = [
	{ folderName: 'cs', id: 'cs' },
	{ folderName: 'de', id: 'de' },
	{ folderName: 'es', id: 'es' },
	{ folderName: 'fr', id: 'fr' },
	{ folderName: 'it', id: 'it' },
	{ folderName: 'ja', id: 'ja' },
	{ folderName: 'ko', id: 'ko' },
	{ folderName: 'pl', id: 'pl' },
	{ folderName: 'pt-br', id: 'pt-br' },
	{ folderName: 'ru', id: 'ru' },
	{ folderName: 'tr', id: 'tr' },
	{ folderName: 'zh-hans', id: 'zh-cn' },
	{ folderName: 'zh-hant', id: 'zh-tw' },
]

const rootDir = __dirname
const l10nDir = path.join(rootDir, 'l10n')
const xlfDir = path.join(rootDir, 'loc', 'xlf')

// Export localizations from TypeScript source files to bundle.l10n.json and generate XLF
const exportL10n = (done) => {
	try {
		// Create l10n directory if it doesn't exist
		if (!fs.existsSync(l10nDir)) {
			fs.mkdirSync(l10nDir, { recursive: true });
		}

		// Step 1: Export strings from source files to bundle.l10n.json
		console.log('Exporting l10n strings from source files...');
		execFileSync('npx', ['@vscode/l10n-dev', 'export', '--outDir', l10nDir, './src'], {
			cwd: rootDir,
			stdio: 'inherit'
		});

		// Create xlf directory if it doesn't exist
		if (!fs.existsSync(xlfDir)) {
			fs.mkdirSync(xlfDir, { recursive: true });
		}

		// Step 2: Generate XLF file from package.nls.json using library function
		console.log('Generating XLF file...');
		const packageNlsPath = path.join(rootDir, 'package.nls.json');
		const xlfPath = path.join(xlfDir, 'aspire-vscode.xlf');

		// Generate XLF from package.nls.json only (not bundle) to get human-readable IDs
		if (fs.existsSync(packageNlsPath)) {
			// Read package.nls.json - it's already in the correct l10nJsonFormat
			const packageNlsContent = JSON.parse(fs.readFileSync(packageNlsPath, 'utf8'));

			// Create map for getL10nXlf - Map<string, l10nJsonFormat>
			const l10nMap = new Map();
			l10nMap.set('package', packageNlsContent);

			// Generate XLF string using library function
			const xlfContent = getL10nXlf(l10nMap);

			// Write XLF file
			fs.writeFileSync(xlfPath, xlfContent);
			console.log(`  Generated: aspire-vscode.xlf`);
		} else {
			throw new Error('package.nls.json not found. Cannot generate XLF.');
		}

		console.log('L10n export complete!');
		done();
	} catch (error) {
		done(error);
	}
};

// Import translations from XLF files and generate localized package.nls.*.json files
const importL10n = async (done) => {
	try {
		const packageNlsPath = path.join(rootDir, 'package.nls.json');
		if (!fs.existsSync(packageNlsPath)) {
			console.log('No package.nls.json found. Run the prepare task first.');
			return done();
		}

		// For each language, import from XLF if it exists
		for (const lang of languages) {
			const xlfPath = path.join(xlfDir, `aspire-vscode.${lang.folderName}.xlf`);
			if (fs.existsSync(xlfPath)) {
				console.log(`Importing translations for ${lang.folderName}...`);

				// Read the XLF file
				const xlfContents = fs.readFileSync(xlfPath, 'utf8');

				// Parse XLF and get localized data
				const l10nDetails = await getL10nFilesFromXlf(xlfContents);

				// Write out the localized files
				for (const detail of l10nDetails) {
					const fileName = detail.name === 'package'
						? `package.nls.${detail.language}.json`
						: `${detail.name}.l10n.${detail.language}.json`;

					const outPath = detail.name === 'package'
						? path.join(rootDir, fileName)
						: path.join(l10nDir, fileName);

					fs.writeFileSync(outPath, JSON.stringify(detail.messages, null, 2));
					console.log(`  Written: ${fileName}`);
				}
			}
		}

		console.log('L10n import complete!');
		done();
	} catch (error) {
		console.error('Error importing translations:', error);
		done(error);
	}
};

const prepareLocFiles = gulp.series(exportL10n);
const generateAllLocalizationFiles = gulp.series(importL10n);

module.exports.prepare = prepareLocFiles;
module.exports.localizationBundle = generateAllLocalizationFiles;
