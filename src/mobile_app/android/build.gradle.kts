allprojects {
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.layout.buildDirectory.set(file("../build"))

subprojects {
    // Avoid cross-drive relative path errors on Windows when the pub cache
    // is on a different drive letter than the project.
    val rootDrive = rootProject.projectDir.toPath().root
    val subDrive = project.projectDir.toPath().root
    if (rootDrive == subDrive) {
        project.layout.buildDirectory.set(rootProject.layout.buildDirectory.dir(project.name))
    } else {
        project.layout.buildDirectory.set(file("${project.projectDir}/build"))
    }
}

subprojects {
    project.evaluationDependsOn(":app")
}

tasks.register<Delete>("clean") {
    delete(rootProject.layout.buildDirectory)
}

