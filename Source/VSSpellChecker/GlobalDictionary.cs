﻿//===============================================================================================================
// System  : Visual Studio Spell Checker Package
// File    : GlobalDictionary.cs
// Author  : Eric Woodruff  (Eric@EWoodruff.us)
// Updated : 02/16/2015
// Note    : Copyright 2013-2015, Eric Woodruff, All rights reserved
// Compiler: Microsoft Visual C#
//
// This file contains a class that implements the global dictionary
//
// This code is published under the Microsoft Public License (Ms-PL).  A copy of the license should be
// distributed with the code and can be found at the project website: https://github.com/EWSoftware/VSSpellChecker
// This notice, the author's name, and all copyright notices must remain intact in all applications,
// documentation, and source files.
//
//    Date     Who  Comments
//===============================================================================================================
// 04/14/2013  EFW  Created the code
//===============================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

using NHunspell;

using VisualStudio.SpellChecker.Configuration;

namespace VisualStudio.SpellChecker
{
    /// <summary>
    /// This class implements the global dictionary
    /// </summary>
    internal sealed class GlobalDictionary : IDisposable
    {
        #region Private data members
        //=====================================================================

        private static Dictionary<string, GlobalDictionary> globalDictionaries;
        private static SpellEngine spellEngine;

        private IServiceProvider serviceProvider;
        private List<WeakReference> registeredServices;
        private HashSet<string> dictionaryWords, ignoredWords;
        private CultureInfo culture;
        private SpellFactory spellFactory;
        private string dictionaryFile, dictionaryWordsFile;

        #endregion

        #region Constructor
        //=====================================================================

        /// <summary>
        /// Private constructor
        /// </summary>
        /// <param name="culture">The language to use for the dictionary</param>
        /// <param name="spellFactory">The spell factory to use when checking words</param>
        /// <param name="dictionaryFile">The dictionary file</param>
        /// <param name="userWordsFile">The user dictionary words file</param>
        /// <param name="serviceProvider">A service provider for interacting with the solution/project</param>
        private GlobalDictionary(CultureInfo culture, SpellFactory spellFactory, string dictionaryFile,
          string userWordsFile, IServiceProvider serviceProvider)
        {
            this.culture = culture;
            this.spellFactory = spellFactory;
            this.dictionaryFile = dictionaryFile;
            this.serviceProvider = serviceProvider;

            if(String.IsNullOrWhiteSpace(dictionaryFile))
                throw new ArgumentException("Dictionary filename cannot be null or empty", "dictionaryFile");

            registeredServices = new List<WeakReference>();

            dictionaryWords = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            if(String.IsNullOrWhiteSpace(userWordsFile))
                dictionaryWordsFile = Path.Combine(SpellingConfigurationFile.GlobalConfigurationFilePath,
                    culture.Name + "_User.dic");
            else
                dictionaryWordsFile = userWordsFile;

            ignoredWords = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

            this.LoadUserDictionaryFile();
        }
        #endregion

        #region IDisposable members
        //=====================================================================

        /// <inheritdoc />
        public void Dispose()
        {
            if(spellFactory != null)
            {
                spellFactory.Dispose();
                spellFactory = null;

                registeredServices.Clear();
            }
        }
        #endregion

        #region Dictionary service interaction methods
        //=====================================================================

        /// <summary>
        /// This is used to spell check a word
        /// </summary>
        /// <param name="word">The word to spell check</param>
        /// <returns>True if spelled correctly, false if not</returns>
        public bool IsSpelledCorrectly(string word)
        {
            try
            {
                if(spellFactory != null && !spellFactory.IsDisposed && !String.IsNullOrWhiteSpace(word))
                    return spellFactory.Spell(word);
            }
            catch(Exception ex)
            {
                // Ignore exceptions, there's not much we can do
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return true;
        }

        /// <summary>
        /// This is used to suggest corrections for a misspelled word
        /// </summary>
        /// <param name="word">The misspelled word for which to get suggestions</param>
        /// <returns>An enumerable list of zero or more suggested correct spellings</returns>
        public IEnumerable<string> SuggestCorrections(string word)
        {
            List<string> suggestions = null;

            try
            {
                if(spellFactory != null && !spellFactory.IsDisposed && !String.IsNullOrWhiteSpace(word))
                    suggestions = spellFactory.Suggest(word);
            }
            catch(Exception ex)
            {
                // Ignore exceptions, there's not much we can do
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return (suggestions ?? new List<string>());
        }

        /// <summary>
        /// Add the given word to the dictionary so that it will no longer show up as an incorrect spelling.
        /// </summary>
        /// <param name="word">The word to add to the dictionary.</param>
        /// <returns><c>true</c> if the word was successfully added to the dictionary, even if it was already in
        /// the dictionary.</returns>
        public bool AddWordToDictionary(string word)
        {
            if(String.IsNullOrWhiteSpace(word))
                return false;

            if(this.ShouldIgnoreWord(word))
                return true;

            if(!dictionaryWordsFile.CanWriteToUserWordsFile(dictionaryFile, serviceProvider))
                return false;

            using(StreamWriter writer = new StreamWriter(dictionaryWordsFile, true))
            {
                writer.WriteLine(word);
            }

            lock(dictionaryWords)
            {
                dictionaryWords.Add(word);
            }

            this.AddSuggestion(word);
            this.NotifySpellingServicesOfChange(word);

            return true;
        }

        /// <summary>
        /// Ignore all occurrences of the given word, but don't add it to the dictionary.
        /// </summary>
        /// <param name="word">The word to be ignored.</param>
        /// <returns><c>true</c> if the word was successfully marked as ignored.</returns>
        public bool IgnoreWord(string word)
        {
            if(String.IsNullOrWhiteSpace(word) || this.ShouldIgnoreWord(word))
                return true;

            lock(ignoredWords)
            {
                ignoredWords.Add(word);
            }

            this.NotifySpellingServicesOfChange(word);

            return true;
        }

        /// <summary>
        /// Check the ignored words dictionary for the given word.
        /// </summary>
        /// <param name="word">The word for which to check</param>
        /// <returns>True if the word should be ignored, false if not</returns>
        public bool ShouldIgnoreWord(string word)
        {
            lock(ignoredWords)
            {
                return ignoredWords.Contains(word);
            }
        }
        #endregion

        #region General methods
        //=====================================================================

        /// <summary>
        /// Create a global dictionary for the specified culture
        /// </summary>
        /// <param name="culture">The language to use for the dictionary.</param>
        /// <param name="serviceProvider">A service provider used to interact with the solution/project</param>
        /// <param name="additionalDictionaryFolders">An enumerable list of additional folders to search for
        /// other dictionaries.</param>
        /// <returns>The global dictionary to use or null if one could not be created.</returns>
        public static GlobalDictionary CreateGlobalDictionary(CultureInfo culture, IServiceProvider serviceProvider,
          IEnumerable<string> additionalDictionaryFolders)
        {
            GlobalDictionary globalDictionary = null;
            string affixFile, dictionaryFile, userWordsFile;

            try
            {
                // The configuration editor should disallow creating a configuration without at least one
                // language but if someone edits the file manually, they could remove them all.  If that
                // happens, just use the English-US dictionary.
                if(culture == null)
                    culture = new CultureInfo("en-US");

                if(globalDictionaries == null)
                    globalDictionaries = new Dictionary<string, GlobalDictionary>();

                // If not already loaded, create the dictionary and the thread-safe spell factory instance for
                // the given culture.
                if(!globalDictionaries.ContainsKey(culture.Name))
                {
                    string dllPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        "NHunspell");

                    if(spellEngine == null)
                    {
                        Hunspell.NativeDllPath = dllPath;
                        spellEngine = new SpellEngine();
                    }

                    // Look for all available dictionaries and get the one for the requested culture
                    var dictionaries = SpellCheckerDictionary.AvailableDictionaries(additionalDictionaryFolders);
                    SpellCheckerDictionary userDictionary;

                    if(dictionaries.TryGetValue(culture.Name, out userDictionary))
                    {
                        affixFile = userDictionary.AffixFilePath;
                        dictionaryFile = userDictionary.DictionaryFilePath;
                        userWordsFile = userDictionary.UserDictionaryFilePath;
                    }
                    else
                        affixFile = dictionaryFile = userWordsFile = null;

                    // If not found, default to the English dictionary supplied with the package.  This can at
                    // least clue us in that it didn't find the language-specific dictionary when the suggestions
                    // are in English.
                    if(affixFile == null || dictionaryFile == null || !File.Exists(affixFile) ||
                      !File.Exists(dictionaryFile))
                    {
                        affixFile = Path.Combine(dllPath, "en_US.aff");
                        dictionaryFile = Path.ChangeExtension(affixFile, ".dic");
                        userWordsFile = Path.Combine(SpellingConfigurationFile.GlobalConfigurationFilePath,
                            "en-US_User.dic");
                    }

                    spellEngine.AddLanguage(new LanguageConfig
                    {
                        LanguageCode = culture.Name,
                        HunspellAffFile = affixFile,
                        HunspellDictFile = dictionaryFile
                    });

                    globalDictionaries.Add(culture.Name, new GlobalDictionary(culture,
                        spellEngine[culture.Name], dictionaryFile, userWordsFile, serviceProvider));
                }

                globalDictionary = globalDictionaries[culture.Name];
            }
            catch(Exception ex)
            {
                // Ignore exceptions.  Not much we can do, we just won't spell check anything.
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return globalDictionary;
        }

        /// <summary>
        /// This is called to clear the dictionary cache and dispose of the spelling engine
        /// </summary>
        /// <remarks>This is done whenever a change in solution is detected.  This allows for solution and
        /// project-specific dictionaries to override global dictionaries that may have been in use in a previous
        /// solution.</remarks>
        public static void ClearDictionaryCache()
        {
            if(globalDictionaries != null)
            {
                foreach(var gd in globalDictionaries.Values)
                    gd.Dispose();

                globalDictionaries.Clear();
            }

            if(spellEngine != null)
            {
                spellEngine.Dispose();
                spellEngine = null;
            }
        }

        /// <summary>
        /// This is used to register a spelling dictionary service with the global dictionary so that it is
        /// notified of changes to the global dictionary.
        /// </summary>
        /// <param name="service">The dictionary service to register</param>
        public void RegisterSpellingDictionaryService(SpellingDictionary service)
        {
            // Clear out ones that have been disposed of
            foreach(var svc in registeredServices.Where(s => !s.IsAlive).ToArray())
                registeredServices.Remove(svc);

            System.Diagnostics.Debug.WriteLine("Registered services count: {0}", registeredServices.Count);

            registeredServices.Add(new WeakReference(service));
        }

        /// <summary>
        /// This is used to notify all registered spelling dictionary services of a change to the global
        /// dictionary.
        /// </summary>
        /// <param name="word">The word that triggered the change</param>
        private void NotifySpellingServicesOfChange(string word)
        {
            // Clear out ones that have been disposed of
            foreach(var service in registeredServices.Where(s => !s.IsAlive).ToArray())
                registeredServices.Remove(service);

            System.Diagnostics.Debug.WriteLine("Registered services count: {0}", registeredServices.Count);

            foreach(var service in registeredServices)
            {
                var target = service.Target as SpellingDictionary;

                if(target != null)
                    target.GlobalDictionaryUpdated(word);
            }
        }

        /// <summary>
        /// This is used to load the user dictionary words file for a specific language if it exists
        /// </summary>
        public static void LoadUserDictionaryFile(CultureInfo language)
        {
            GlobalDictionary g;

            if(globalDictionaries != null && globalDictionaries.TryGetValue(language.Name, out g))
            {
                g.LoadUserDictionaryFile();
                g.NotifySpellingServicesOfChange(null);
            }
        }

        /// <summary>
        /// This is used to load the user dictionary words file
        /// </summary>
        private void LoadUserDictionaryFile()
        {
            dictionaryWords.Clear();

            if(File.Exists(dictionaryWordsFile))
            {
                try
                {
                    foreach(string word in File.ReadLines(dictionaryWordsFile))
                        if(!String.IsNullOrWhiteSpace(word))
                            dictionaryWords.Add(word.Trim());

                    this.AddSuggestions();
                }
                catch(Exception ex)
                {
                    // Ignore exceptions.  Not much we can do, we'll just not ignore anything by default.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
            else
            {
                // Older versions used a different filename.  If found, rename it and use it.
                string oldFilename = dictionaryWordsFile.Replace("_User", "_Ignored");

                try
                {
                    if(File.Exists(oldFilename))
                    {
                        if(File.Exists(dictionaryWordsFile))
                            File.Delete(dictionaryWordsFile);

                        File.Move(oldFilename, dictionaryWordsFile);
                        LoadUserDictionaryFile();
                    }
                }
                catch(Exception ex)
                {
                    // Ignore exceptions.  We just won't load the old file.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }

        /// <summary>
        /// Add a new word as a suggestion to the Hunspell instances
        /// </summary>
        /// <remarks>The word is not added to the Hunspell dictionary files, just the speller instances</remarks>
        private void AddSuggestion(string word)
        {
            // Since we're using the factory, we've got to get at the internals using reflection
            Type sf = spellFactory.GetType();

            FieldInfo fi = sf.GetField("processors", BindingFlags.Instance | BindingFlags.NonPublic);

            int releaseCount = 0, processors = (int)fi.GetValue(spellFactory);

            fi = sf.GetField("hunspellSemaphore", BindingFlags.Instance | BindingFlags.NonPublic);

            Semaphore hunspellSemaphore = (Semaphore)fi.GetValue(spellFactory);

            fi = sf.GetField("hunspells", BindingFlags.Instance | BindingFlags.NonPublic);

            Stack<Hunspell> hunspells = (Stack<Hunspell>)fi.GetValue(spellFactory);

            if(hunspellSemaphore != null && hunspells != null)
                try
                {
                    // Make sure we get all semaphores since we will be touching all spellers
                    while(releaseCount < processors)
                    {
                        // Don't wait too long.  If we can't get them all, we just won't add the words
                        // as suggestions this time around.
                        if(!hunspellSemaphore.WaitOne(2000))
                            break;

                        releaseCount++;
                    }

                    if(releaseCount == processors)
                        foreach(var hs in hunspells.ToArray())
                            if(!hs.Spell(word))
                                hs.Add(word.ToLower(culture));
                }
                catch(Exception ex)
                {
                    // Ignore any exceptions.  Worst case, some words won't be added as suggestions.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
                finally
                {
                    if(releaseCount != 0)
                        hunspellSemaphore.Release(releaseCount);
                }
        }

        /// <summary>
        /// Add the user dictionary words as suggestions to the Hunspell instances
        /// </summary>
        /// <remarks>The words are not added to the Hunspell dictionary files, just the speller instances</remarks>
        private void AddSuggestions()
        {
            // Since we're using the factory, we've got to get at the internals using reflection
            Type sf = spellFactory.GetType();

            FieldInfo fi = sf.GetField("processors", BindingFlags.Instance | BindingFlags.NonPublic);

            int releaseCount = 0, processors = (int)fi.GetValue(spellFactory);

            fi = sf.GetField("hunspellSemaphore", BindingFlags.Instance | BindingFlags.NonPublic);

            Semaphore hunspellSemaphore = (Semaphore)fi.GetValue(spellFactory);

            fi = sf.GetField("hunspells", BindingFlags.Instance | BindingFlags.NonPublic);

            Stack<Hunspell> hunspells = (Stack<Hunspell>)fi.GetValue(spellFactory);

            if(hunspellSemaphore != null && hunspells != null)
                try
                {
                    // Make sure we get all semaphores since we will be touching all spellers
                    while(releaseCount < processors)
                    {
                        // Don't wait too long.  If we can't get them all, we just won't add the words
                        // as suggestions this time around.
                        if(!hunspellSemaphore.WaitOne(2000))
                            break;

                        releaseCount++;
                    }

                    if(releaseCount == processors)
                        foreach(var hs in hunspells.ToArray())
                            foreach(string word in dictionaryWords)
                                if(!hs.Spell(word))
                                    hs.Add(word.ToLower(culture));
                }
                catch(Exception ex)
                {
                    // Ignore any exceptions.  Worst case, some words won't be added as suggestions.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
                finally
                {
                    if(releaseCount != 0)
                        hunspellSemaphore.Release(releaseCount);
                }
        }

        /// <summary>
        /// Remove the given word from the global dictionaries
        /// </summary>
        /// <param name="word">The word to remove</param>
        public static void RemoveWord(CultureInfo language, string word)
        {
            GlobalDictionary g;

            if(!String.IsNullOrWhiteSpace(word) && globalDictionaries != null &&
              globalDictionaries.TryGetValue(language.Name, out g))
                g.RemoveWord(word);
        }

        /// <summary>
        /// Remove the given word from the Hunspell instances 
        /// </summary>
        /// <param name="word">The word to remove</param>
        /// <remarks>The word is not removed from the Hunspell dictionary files, just the speller instances</remarks>
        private void RemoveWord(string word)
        {
            // Since we're using the factory, we've got to get at the internals using reflection
            Type sf = spellFactory.GetType();

            FieldInfo fi = sf.GetField("processors", BindingFlags.Instance | BindingFlags.NonPublic);

            int releaseCount = 0, processors = (int)fi.GetValue(spellFactory);

            fi = sf.GetField("hunspellSemaphore", BindingFlags.Instance | BindingFlags.NonPublic);

            Semaphore hunspellSemaphore = (Semaphore)fi.GetValue(spellFactory);

            fi = sf.GetField("hunspells", BindingFlags.Instance | BindingFlags.NonPublic);

            Stack<Hunspell> hunspells = (Stack<Hunspell>)fi.GetValue(spellFactory);

            if(hunspellSemaphore != null && hunspells != null)
                try
                {
                    // Make sure we get all semaphores since we will be touching all spellers
                    while(releaseCount < processors)
                    {
                        // Don't wait too long.  If we can't get them all, we just won't add the words
                        // as suggestions this time around.
                        if(!hunspellSemaphore.WaitOne(2000))
                            break;

                        releaseCount++;
                    }

                    if(releaseCount == processors)
                        foreach(var hs in hunspells.ToArray())
                            if(hs.Spell(word))
                                hs.Remove(word.ToLower(culture));
                }
                catch(Exception ex)
                {
                    // Ignore any exceptions.  Worst case, some words won't be added as suggestions.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
                finally
                {
                    if(releaseCount != 0)
                        hunspellSemaphore.Release(releaseCount);
                }
        }
        #endregion
    }
}
