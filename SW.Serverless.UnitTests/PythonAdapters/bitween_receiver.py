"""A Bitween receiver over a folder, written with simplyworks_bitween."""
import os

import simplyworks_serverless as sw
from simplyworks_bitween import ExchangeFile, Receiver


class Folder(Receiver):
    def __init__(self):
        sw.expect("Folder")
        self.calls = []

    @property
    def folder(self):
        return sw.value_of("Folder")

    def initialize(self):
        self.calls.append("Initialize")

    def list_files(self):
        self.calls.append("ListFiles")
        return sorted(os.listdir(self.folder))

    def get_file(self, file_id):
        self.calls.append("GetFile")
        with open(os.path.join(self.folder, file_id), encoding="utf-8") as f:
            return ExchangeFile(data=f.read(), filename=file_id)

    def delete_file(self, file_id):
        self.calls.append("DeleteFile")
        os.remove(os.path.join(self.folder, file_id))

    def finalize(self):
        self.calls.append("Finalize")
        with open(os.path.join(self.folder, "..", "calls.txt"), "w", encoding="utf-8") as f:
            f.write(",".join(self.calls))


if __name__ == "__main__":
    sw.run(Folder)
