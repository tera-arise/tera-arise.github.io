#addin nuget:?package=Cake.Npm&version=2.0.0

#nullable enable

// Arguments

var target = Argument("t", "default");

// Tasks

Task("default")
    .IsDependentOn("build");

Task("default-editor")
    .IsDependentOn("build");

Task("restore-core")
    .Does(NpmInstall);

Task("restore")
    .IsDependentOn("restore-core");

Task("build-core")
    .IsDependentOn("restore-core")
    .Does(() => NpmRunScript("build"));

Task("build")
    .IsDependentOn("build-core");

RunTarget(target);
