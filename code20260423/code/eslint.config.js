import vueParser from "vue-eslint-parser";
import tsParser from "@typescript-eslint/parser";

export default [
	// Vue files
	{
		files: ["**/*.vue"],
		languageOptions: {
			ecmaVersion: "latest",
			sourceType: "module",
			parser: vueParser,
			parserOptions: {
				parser: tsParser,
				ecmaVersion: "latest",
				sourceType: "module",
			},
		},
		rules: {
			//   'no-console': 'warn',
			"no-console": "off",
			"no-debugger": "warn",
		},
	},
	// TypeScript files
	{
		files: ["**/*.ts"],
		languageOptions: {
			ecmaVersion: "latest",
			sourceType: "module",
			parser: tsParser,
		},
		rules: {
			"no-console": "warn",
			"no-debugger": "warn",
		},
	},
	// JavaScript files
	{
		files: ["**/*.js"],
		languageOptions: {
			ecmaVersion: "latest",
			sourceType: "module",
		},
		rules: {
			"no-console": "warn",
			"no-debugger": "warn",
		},
	},
];
