﻿using System;
using System.IO;
using GitHub.Models;

namespace GitHub.ViewModels
{
    /// <summary>
    /// A file node in a pull request changes tree.
    /// </summary>
    public class PullRequestFileNode : IPullRequestFileNode
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PullRequestFileNode"/> class.
        /// </summary>
        /// <param name="repositoryPath">The absolute path to the repository.</param>
        /// <param name="path">The path to the file, relative to the repository.</param>
        /// <param name="changeType">The way the file was changed.</param>
        public PullRequestFileNode(string repositoryPath, string path, PullRequestFileStatus status)
        {
            FileName = Path.GetFileName(path);
            DirectoryPath = Path.GetDirectoryName(path);
            DisplayPath = Path.Combine(Path.GetFileName(repositoryPath), DirectoryPath);
            Status = status;
        }

        /// <summary>
        /// Gets the name of the file without path information.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Gets the path to the file's directory, relative to the root of the repository.
        /// </summary>
        public string DirectoryPath { get; }

        /// <summary>
        /// Gets the path to display in the "Path" column of the changed files list.
        /// </summary>
        public string DisplayPath { get; }

        /// <summary>
        /// Gets the type of change that was made to the file.
        /// </summary>
        public PullRequestFileStatus Status { get; }
    }
}
