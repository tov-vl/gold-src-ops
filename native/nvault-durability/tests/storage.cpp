// SPDX-License-Identifier: GPL-3.0-or-later
#include "NVault.h"
#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <sys/wait.h>
#include <signal.h>
using namespace process_io;
static int tests = 0;
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"STORAGE_ASSERTION_FAILED line=%d expression=%s\n",__LINE__,#x); abort(); } } while (0)
static std::string root;
static std::string Path(const char *name) {
    std::string dir = root + "/" + name;
    CHECK(mkdir(dir.c_str(), 0700) == 0);
    return dir + "/players.vault";
}
static std::vector<unsigned char> Bytes(const std::string &path) {
    std::vector<unsigned char> b; bool missing;
    CHECK(Read(path.c_str(), MaxVault, b, missing) && !missing); return b;
}
static void PutBytes(const std::string &path, const std::vector<unsigned char> &b) {
    FILE *f = fopen(path.c_str(), "wb"); CHECK(f);
    CHECK(Write(f,b,"none") && Flush(f,"none") && process_io::Close(f,"none"));
}
static void CheckValue(NVault &v, const char *key, const char *value, time_t expected = 111) {
    char b[65536]; time_t stamp;
    CHECK(v.GetValue(key,stamp,b,sizeof b) && !strcmp(b,value) && stamp==expected);
}
static void Seed(const std::string &path) {
    NVault v(path.c_str()); CHECK(v.Open());
    v.SetValue("player-a","old-a",100); v.SetValue("player-b","old-b",100);
    CHECK(v.Commit() && v.Close());
}
static void Pending(const std::string &path) {
    pid_t pid=fork(); CHECK(pid>=0);
    if (!pid) {
        NVault v(path.c_str()); CHECK(v.Open());
        v.SetValue("player-a","new-a",111); v.SetValue("player-b","new-b",111);
        CHECK(v.Commit()); kill(getpid(),SIGKILL); _exit(99);
    }
    int status; CHECK(waitpid(pid,&status,0)==pid && WIFSIGNALED(status) && WTERMSIG(status)==SIGKILL);
}
static void KillOpen(const std::string &path,const char *point) {
    pid_t pid=fork(); CHECK(pid>=0);
    if (!pid) { setenv("NVAULT_KILL",point,1); NVault v(path.c_str()); v.Open(); _exit(98); }
    int status; CHECK(waitpid(pid,&status,0)==pid && WIFSIGNALED(status) && WTERMSIG(status)==SIGKILL);
}
static void CheckRecovered(const std::string &path, bool allowUnconfirmed = false) {
    NVault v(path.c_str()); CHECK(v.Open());
    CheckValue(v,"player-a","new-a"); CheckValue(v,"player-b","new-b");
    CHECK(v.Items()==2 || (allowUnconfirmed && v.Items()==3 && !strcmp(v.GetValue("unconfirmed"),"not-acknowledged")));
    CHECK(!strcmp(v.GetValue("after-failure"),""));
    CHECK(v.Close());
    NVault again(path.c_str()); CHECK(again.Open());
    CheckValue(again,"player-a","new-a"); CheckValue(again,"player-b","new-b"); CHECK(again.Items()==v.Items());
}
static void CrashTransitions() {
    const char *points[]={"replay_complete","checkpoint_created","checkpoint_written","checkpoint_flushed",
        "checkpoint_closed","checkpoint_verified","checkpoint_published","journal_erased","journal_ready"};
    for (const char *point:points) {
        std::string path=Path(point); Seed(path); Pending(path);
        KillOpen(path,point); KillOpen(path,"checkpoint_written"); CheckRecovered(path); tests++;
    }
}
static void IoFailures() {
    const char *points[]={"journal_write","journal_flush","journal_close","checkpoint_open","checkpoint_write",
        "checkpoint_flush","checkpoint_close","checkpoint_verify","checkpoint_rename","journal_erase"};
    for (const char *point:points) {
        std::string path=Path(point); Seed(path);
        {
            NVault v(path.c_str()); CHECK(v.Open());
            v.SetValue("player-a","new-a",111); v.SetValue("player-b","new-b",111); CHECK(v.Commit());
            setenv("NVAULT_FAIL",point,1);
            if (!strcmp(point,"journal_write") || !strcmp(point,"journal_flush")) {
                v.SetValue("unconfirmed","not-acknowledged",112); CHECK(!v.Commit());
                v.SetValue("after-failure","must-not-append",113); CHECK(!v.Commit());
            }
            CHECK(!v.Close()); unsetenv("NVAULT_FAIL");
        }
        CheckRecovered(path, true); tests++;
    }
    std::string path=Path("journal_open"); Seed(path); Pending(path);
    { setenv("NVAULT_FAIL","journal_open",1); NVault v(path.c_str()); CHECK(!v.Open()); unsetenv("NVAULT_FAIL"); }
    CheckRecovered(path); tests++;
}
static void BadFiles() {
    std::string path=Path("corrupt-journal"); Seed(path); Pending(path);
    std::string journal=path.substr(0,path.size()-6)+".journal";
    auto bytes=Bytes(journal); bytes[0]=255; PutBytes(journal,bytes);
    { NVault v(path.c_str()); CHECK(!v.Open()); }
    CHECK(Bytes(journal)==bytes); tests++;
    path=Path("truncated-tail"); Seed(path); Pending(path); journal=path.substr(0,path.size()-6)+".journal";
    bytes=Bytes(journal); bytes.push_back(Journal_Insert); bytes.push_back(1); PutBytes(journal,bytes);
    CheckRecovered(path); CHECK(Bytes(journal+".partial-retained")==bytes); tests++;
    path=Path("invalid-vault"); Seed(path); bytes=Bytes(path); bytes[0]=0; PutBytes(path,bytes);
    { NVault v(path.c_str()); CHECK(!v.Open()); } CHECK(Bytes(path)==bytes); tests++;
    path=Path("linked-vault"); std::string target=path+".target"; PutBytes(target,std::vector<unsigned char>(20,'x'));
    CHECK(!symlink(target.c_str(),path.c_str())); { NVault v(path.c_str()); CHECK(!v.Open()); }
    CHECK(Bytes(target)==std::vector<unsigned char>(20,'x')); tests++;
    path=Path("named-pipe");CHECK(!mkfifo(path.c_str(),0600));
    { NVault v(path.c_str());CHECK(!v.Open()); }tests++;
    path=Path("journal-cap"); Seed(path); bytes.assign(MaxJournal+1,'x'); journal=path.substr(0,path.size()-6)+".journal";
    PutBytes(journal,bytes); { NVault v(path.c_str()); CHECK(!v.Open()); } tests++;
}
static std::vector<std::string> State(VaultMap &map) {
    std::vector<std::string> state;
    for (auto i=map.iter();!i.empty();i.next()) state.push_back(std::string(i->key.chars())+":"+i->value.value.chars()+":"+std::to_string(i->value.stamp));
    std::sort(state.begin(),state.end()); return state;
}
static void IdempotentReplay() {
    std::string file=Path("replay-algebra")+".journal";
    for (unsigned sequence=0;sequence<1296;sequence++) {
        Journal j(file.c_str()); CHECK(j.Begin()); unsigned encoded=sequence;
        for (int i=0;i<4;i++) {
            switch (encoded%6) {
                case 0: CHECK(j.Write_Insert("a","ten",10)); break;
                case 1: CHECK(j.Write_Insert("b","permanent",0)); break;
                case 2: CHECK(j.Write_Remove("a")); break;
                case 3: CHECK(j.Write_Clear()); break;
                case 4: CHECK(j.Write_Prune(0,20)); break;
                case 5: CHECK(j.Write_Insert("a","thirty",30)); break;
            }
            encoded/=6;
        }
        CHECK(j.Commit() && j.End());
        VaultMap map; ArrayInfo info;info.value="original";info.stamp=15; CHECK(map.replace("a",info));
        info.stamp=25;CHECK(map.replace("untouched",info));
        CHECK(j.Replay(&map)==4); auto once=State(map); CHECK(j.Replay(&map)==4); CHECK(State(map)==once); CHECK(j.Erase());
    }
    tests++;
}
static void TouchAndPrune() {
    std::string path=Path("touch-prune");
    { NVault v(path.c_str());CHECK(v.Open());v.SetValue("a","a",1);v.SetValue("b","b",1);v.Touch("a",50);CHECK(v.Prune(0,10)==1);CHECK(v.Commit() && v.Close()); }
    { NVault v(path.c_str());CHECK(v.Open());CheckValue(v,"a","a",50);CHECK(!strcmp(v.GetValue("b"),""));CHECK(v.Items()==1); } tests++;
}
static void ReadOnlyAndSpace() {
    std::string path=Path("read-only");Seed(path);
    { NVault v(path.c_str());CHECK(v.Open());v.SetValue("player-a","new-a",111);v.SetValue("player-b","new-b",111);CHECK(v.Commit());
      std::string dir=path.substr(0,path.find_last_of('/')); CHECK(!chmod(dir.c_str(),0500));CHECK(!v.Close());CHECK(!chmod(dir.c_str(),0700)); }
    CheckRecovered(path);tests++;
    char directory[]="/space/nvault-XXXXXX"; CHECK(mkdtemp(directory));path=std::string(directory)+"/players.vault";Seed(path);
    { NVault v(path.c_str());CHECK(v.Open());v.SetValue("player-a","new-a",111);v.SetValue("player-b","new-b",111);CHECK(v.Commit());
      std::string filler=std::string(directory)+"/filler";FILE *f=fopen(filler.c_str(),"wb");CHECK(f);char b[4096]={};
      for (int i=0;i<128 && fwrite(b,1,sizeof b,f)==sizeof b;i++) {} fclose(f);
      std::string big(32768,'x');v.SetValue("unconfirmed",big.c_str(),112);CHECK(!v.Commit());CHECK(!v.Close());CHECK(!unlink(filler.c_str())); }
    CheckRecovered(path);tests++;
}
static void Benchmark() {
    std::string path=Path("benchmark");NVault v(path.c_str());CHECK(v.Open());std::vector<double> times;
    for (int i=0;i<10000;i++) {
        std::string key="player-"+std::to_string(i%32),value=std::to_string(i);
        auto start=std::chrono::steady_clock::now();v.SetValue(key.c_str(),value.c_str(),111);CHECK(v.Commit());
        times.push_back(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count());
    }
    std::sort(times.begin(),times.end());fprintf(stdout,"STORAGE_COMMIT_BENCHMARK samples=10000 identities=32 p99_ms=%.6f max_ms=%.6f\n",times[9899],times.back());
    CHECK(times[9899]<=2.0);tests++;
}
static void RetentionAndCapacity() {
    std::string path=Path("linked-checkpoint");Seed(path);Pending(path);
    std::string target=path+".target";PutBytes(target,std::vector<unsigned char>(12,'x'));
    CHECK(!symlink(target.c_str(),(path+".checkpoint").c_str()));
    { NVault v(path.c_str());CHECK(!v.Open()); } CHECK(Bytes(target)==std::vector<unsigned char>(12,'x'));tests++;
    path=Path("retention-cap");Seed(path);Pending(path);PutBytes(path+".checkpoint",std::vector<unsigned char>(5,'x'));
    for (int i=0;i<8;i++) PutBytes(path+".checkpoint.retained-"+std::to_string(i),std::vector<unsigned char>(5,'y'));
    auto old=Bytes(path);{ NVault v(path.c_str());CHECK(!v.Open()); }CHECK(Bytes(path)==old);tests++;
    path=Path("vault-input-cap");PutBytes(path,std::vector<unsigned char>(MaxVault+1,'x'));
    { NVault v(path.c_str());CHECK(!v.Open()); }tests++;
    path=Path("entries-cap");
    { NVault v(path.c_str());CHECK(v.Open());
      for (size_t i=0;i<MaxEntries;i++) { std::string key="key-"+std::to_string(i);v.SetValue(key.c_str(),"value",111);CHECK(v.Commit()); }
      v.SetValue("overflow","not-acknowledged",111);CHECK(!v.Commit());CHECK(!v.Close()); }
    { NVault v(path.c_str());CHECK(v.Open());CHECK(v.Items()==MaxEntries);CheckValue(v,"key-65535","value");CHECK(!strcmp(v.GetValue("overflow"),"")); }tests++;
}
static void SlowIo() {
    std::string path=Path("slow-io");NVault v(path.c_str());CHECK(v.Open());std::vector<double> times;
    setenv("NVAULT_SLOW_IO","1",1);
    for (int i=0;i<100;i++) {
        auto start=std::chrono::steady_clock::now();v.SetValue("a","value",111);CHECK(v.Commit());
        times.push_back(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count());
    }
    unsetenv("NVAULT_SLOW_IO");std::sort(times.begin(),times.end());CHECK(times[98]>2.0);CheckValue(v,"a","value");
    printf("STORAGE_SLOW_IO=expected_target_miss samples=100 p99_ms=%.6f committed_values=preserved\n",times[98]);tests++;
}
int main() {
    char directory[]="/tmp/nvault-storage-XXXXXX";CHECK(mkdtemp(directory));root=directory;
    CrashTransitions();IoFailures();BadFiles();IdempotentReplay();TouchAndPrune();ReadOnlyAndSpace();RetentionAndCapacity();Benchmark();SlowIo();
    printf("NVAULT_STORAGE_TESTS=passed cases=%d replay_sequences=1296 failures=0\n",tests);return 0;
}
