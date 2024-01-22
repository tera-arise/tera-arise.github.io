/** @param {import("@11ty/eleventy").UserConfig} config */
module.exports = function(config) {
    config.addPlugin(require("@11ty/eleventy-plugin-directory-output"));

    config.addPassthroughCopy({
        "arise.*": "images",
        "src/images": ""
    });

    return {
        dir: {
            templateFormats: [
                "md",
                "njk",
            ],
            input: "src",
            layouts: "layouts",
            output: "out/bin",
        }
    }
};
